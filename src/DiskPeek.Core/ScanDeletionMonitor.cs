using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskPeek.Core;

/// <summary>Monotonic, path-based tombstones for an immutable scan snapshot.</summary>
public sealed class ScanDeletionState
{
    private readonly int[] deleted;
    private readonly int[] subtreeCounts;
    private readonly DeletionTrackingHealth health;
    private int total;
    internal ScanDeletionState(int count, DeletionTrackingHealth health)
    { deleted = new int[count]; subtreeCounts = new int[count]; this.health = health; }
    public int Count => deleted.Length;
    public int DeletedCount => Volatile.Read(ref total);
    public bool TrackingIncomplete => Volatile.Read(ref health.IncompleteReason) != null;
    public string Status => Volatile.Read(ref health.Stopped) != 0 ? "Deletion tracking stopped."
        : Volatile.Read(ref health.IncompleteReason) is { } reason ? "Tracking incomplete: " + reason + " Rescan to refresh."
        : Volatile.Read(ref health.Attached) == 0 ? "Watching for changes while the scan runs."
        : "Watching for deletions or moves since this scan (best effort).";
    public bool IsDeleted(int index) => (uint)index < (uint)Count && Volatile.Read(ref deleted[index]) != 0;
    /// <summary>Includes the node itself and every deleted or moved descendant path.</summary>
    public int GetDeletedCount(int index) => (uint)index < (uint)Count ? Volatile.Read(ref subtreeCounts[index]) : 0;
    public int DeletedCountInSubtree(int index) => GetDeletedCount(index);
    public bool HasDeletedDescendants(int index) => GetDeletedCount(index) > (IsDeleted(index) ? 1 : 0);
    internal void SetDeleted(int index) => Volatile.Write(ref deleted[index], 1);
    internal void SetCount(int index, int count) => Volatile.Write(ref subtreeCounts[index], count);
    internal void AddCount(int index, int count) => Interlocked.Add(ref subtreeCounts[index], count);
    internal void AddTotal(int count) => Interlocked.Add(ref total, count);
}

internal sealed class DeletionTrackingHealth
{
    internal string? IncompleteReason;
    internal int Attached;
    internal int Stopped;
}

/// <summary>
/// Buffers filesystem notifications from before a scan, then verifies absence against
/// that snapshot. It never changes scan sizes, follows snapshot reparse subtrees, or polls the tree.
/// </summary>
public sealed class ScanDeletionMonitor : IDisposable, IAsyncDisposable
{
    private readonly string target;
    private readonly string volumeRoot;
    private readonly Func<string, int> metadataError;
    private readonly int queueCapacity;
    private const int MaximumQueuedCharacters = 2 * 1024 * 1024;
    private readonly object gate = new();
    private readonly object publicationGate = new();
    private readonly Queue<string> pending = new();
    private readonly HashSet<string> coalesced = new(StringComparer.Ordinal);
    private readonly List<TaskCompletionSource> flushRequests = [];
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly DeletionTrackingHealth health = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private FileSystemWatcher? watcher;
    private ScanDeletionState state;
    private SnapshotLookup? lookup;
    private Task? attachment;
    private Task? worker;
    private int queuedCharacters;
    private int signalPending;
    private int attachStarted;
    private int disposed;
    private long dirtyVersion;
    private long publishedVersion;
    private long lastPublishedMilliseconds = -100;

    public ScanDeletionMonitor(string target) : this(target, ProbeMetadataError, true, 4096) { }

    // Probe result: 0 means present; only Win32 2/3 mean missing. Tests can model ACL,
    // media loss, and buffer overflow without changing real permissions or drives.
    internal ScanDeletionMonitor(string target, Func<string, int> metadataErrorProbe,
        bool enableWatcher = false, int queueCapacity = 4096)
    {
        if (queueCapacity is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        this.target = NormalizePath(target);
        volumeRoot = NormalizePath(Path.GetPathRoot(this.target) ?? throw new ArgumentException("A rooted scan target is required."));
        metadataError = metadataErrorProbe ?? throw new ArgumentNullException(nameof(metadataErrorProbe));
        this.queueCapacity = queueCapacity;
        state = new(0, health);
        if (!enableWatcher) return;
        try
        {
            watcher = new FileSystemWatcher(this.target)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 32768
            };
            watcher.Deleted += (_, args) => NotifyPath(args.FullPath);
            watcher.Renamed += (_, args) => NotifyPath(args.OldFullPath);
            watcher.Error += (_, args) => ReportWatcherError(args.GetException());
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            watcher?.Dispose(); watcher = null;
            MarkIncomplete("Filesystem notifications are unavailable.");
        }
    }

    public ScanDeletionState State => Volatile.Read(ref state);
    /// <summary>Raised from the worker at most ten times per second. Marshal UI work to its dispatcher.</summary>
    public event EventHandler? Changed;

    public Task AttachAsync(ScanResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!StringComparer.OrdinalIgnoreCase.Equals(target, NormalizePath(result.Target)))
            throw new ArgumentException("The snapshot target must match the watched target.", nameof(result));
        if (result.Count == 0 || result.FirstChild.Length != result.Count || result.NextSibling.Length != result.Count)
            throw new ArgumentException("Attach an aggregated scan snapshot.", nameof(result));
        if (Interlocked.Exchange(ref attachStarted, 1) != 0) throw new InvalidOperationException("A monitor can attach only one scan snapshot.");
        return attachment = AttachCoreAsync(result, cancellationToken);
    }

    private async Task AttachCoreAsync(ScanResult result, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            var next = await Task.Run(() => new SnapshotLookup(result, linked.Token), linked.Token).ConfigureAwait(false);
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                lookup = next;
                Volatile.Write(ref state, new ScanDeletionState(result.Count, health));
                Volatile.Write(ref health.Attached, 1);
                Interlocked.Increment(ref dirtyVersion);
                worker = Task.Run(WorkAsync);
            }
            // Changes captured while scanning/indexing are visible when attachment completes.
            await FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Queue a known changed path, for example after a successful Recycle Bin operation.</summary>
    public void NotifyPath(string fullPath)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        string path;
        try { path = NormalizePath(fullPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return; }
        if (!WithinTarget(path)) return;
        lock (gate)
        {
            if (disposed != 0 || coalesced.Contains(path)) return;
            if (pending.Count >= queueCapacity || path.Length > 32767 || queuedCharacters + path.Length > MaximumQueuedCharacters)
            { MarkIncomplete("The pending change queue overflowed; some changes may have been missed."); Wake(); return; }
            coalesced.Add(path); pending.Enqueue(path); queuedCharacters += path.Length;
        }
        Wake();
    }

    internal void ReportWatcherError(Exception error)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        MarkIncomplete(error is InternalBufferOverflowException
            ? "The filesystem notification buffer overflowed; some changes may have been missed."
            : "Filesystem notifications stopped or failed.");
        // A deleted watched folder can cause Error instead of its own Deleted event.
        // The volume/share availability check prevents a disconnected drive becoming a mass deletion.
        NotifyPath(target);
        Wake();
    }

    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (lookup == null) throw new InvalidOperationException("Attach a snapshot before flushing changes.");
            flushRequests.Add(completion);
        }
        Wake();
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Wake()
    {
        if (Interlocked.Exchange(ref signalPending, 1) == 0) signal.Release();
    }

    private void MarkIncomplete(string reason)
    {
        if (Interlocked.CompareExchange(ref health.IncompleteReason, reason, null) == null)
            Interlocked.Increment(ref dirtyVersion);
    }

    private async Task WorkAsync()
    {
        List<TaskCompletionSource>? activeFlushes = null;
        try
        {
            while (true)
            {
                await signal.WaitAsync(lifetime.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref signalPending, 0);
                List<string> paths;
                lock (gate)
                {
                    paths = new List<string>(pending.Count);
                    while (pending.TryDequeue(out var path)) paths.Add(path);
                    coalesced.Clear(); queuedCharacters = 0;
                    activeFlushes = [.. flushRequests]; flushRequests.Clear();
                }
                if (paths.Count != 0) ProcessPaths(paths, lifetime.Token);
                if (Volatile.Read(ref dirtyVersion) != Volatile.Read(ref publishedVersion))
                {
                    int delay = (int)Math.Max(0, 100 - (clock.ElapsedMilliseconds - lastPublishedMilliseconds));
                    if (delay > 0) await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                    Publish();
                }
                foreach (var completion in activeFlushes) completion.TrySetResult();
                activeFlushes = null;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { MarkIncomplete("The change worker stopped unexpectedly."); Publish(); }
        finally
        {
            if (activeFlushes != null) foreach (var completion in activeFlushes) completion.TrySetCanceled();
            lock (gate)
            {
                foreach (var completion in flushRequests) completion.TrySetCanceled();
                flushRequests.Clear();
            }
        }
    }

    private void ProcessPaths(List<string> paths, CancellationToken cancellationToken)
    {
        if (Probe(volumeRoot) != 0)
        { MarkIncomplete("The scan drive or share is unavailable; absence could not be verified."); return; }
        // Parent deletions first make large duplicate descendant notification bursts cheap.
        paths.Sort((a, b) => a.Length.CompareTo(b.Length));
        var snapshot = lookup!;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = snapshot.Resolve(path, target);
            if (index < 0 || State.IsDeleted(index)) continue;
            int error = Probe(path);
            if (error == 0) continue;
            if (error is not (2 or 3))
            { MarkIncomplete($"A changed path could not be checked (Win32 error {error})."); continue; }
            // Recheck for media loss immediately before accepting an absence result.
            if (Probe(volumeRoot) != 0)
            { MarkIncomplete("The scan drive or share is unavailable; absence could not be verified."); return; }
            if (index == 0 && StringComparer.OrdinalIgnoreCase.Equals(target, volumeRoot)) continue;
            MarkSubtree(snapshot.Result, index);
        }
    }

    private int Probe(string path)
    {
        try { return metadataError(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { return -1; }
    }

    private void MarkSubtree(ScanResult result, int root)
    {
        var current = State;
        int previous = current.GetDeletedCount(root);
        var order = new List<int> { root };
        for (int cursor = 0; cursor < order.Count; cursor++)
        {
            int index = order[cursor];
            current.SetDeleted(index);
            if ((result.Nodes[index].Flags & NodeFlags.ReparseSkipped) == 0)
                foreach (int child in result.Children(index)) order.Add(child);
        }
        // Complete subtree counts bottom-up once, rather than updating every leaf's ancestors.
        for (int step = order.Count - 1; step >= 0; step--)
        {
            int index = order[step], total = 1;
            foreach (int child in result.Children(index)) total = checked(total + current.GetDeletedCount(child));
            current.SetCount(index, total);
        }
        int added = current.GetDeletedCount(root) - previous;
        for (int parent = result.Nodes[root].ParentIndex; parent >= 0; parent = result.Nodes[parent].ParentIndex)
            current.AddCount(parent, added);
        current.AddTotal(added);
        Interlocked.Increment(ref dirtyVersion);
    }

    private void Publish()
    {
        lock (publicationGate)
        {
            if (disposed != 0) return;
            lastPublishedMilliseconds = clock.ElapsedMilliseconds;
            Volatile.Write(ref publishedVersion, Volatile.Read(ref dirtyVersion));
            if (Changed is not { } handlers) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                if (disposed != 0) break;
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
            }
        }
    }

    private bool WithinTarget(string path) => StringComparer.OrdinalIgnoreCase.Equals(path, target) ||
        (path.Length > target.Length && path.StartsWith(target, StringComparison.OrdinalIgnoreCase) &&
         (Path.EndsInDirectorySeparator(target) || path[target.Length] == Path.DirectorySeparatorChar));

    private static string NormalizePath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full[8..];
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full[4..];
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static int ProbeMetadataError(string path)
    {
        uint attributes = GetFileAttributesW(NativeDirectory.ExtendedPath(path));
        return attributes == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);

    public void Dispose()
    {
        lock (publicationGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Volatile.Write(ref health.Stopped, 1);
        }
        lifetime.Cancel();
        watcher?.Dispose(); watcher = null;
        lock (gate)
        {
            pending.Clear(); coalesced.Clear(); queuedCharacters = 0;
            foreach (var completion in flushRequests) completion.TrySetCanceled();
            flushRequests.Clear();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        var attaching = attachment;
        if (attaching != null) try { await attaching.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        var running = worker;
        if (running != null) await running.ConfigureAwait(false);
    }

    private readonly record struct NodeKey(int Parent, string Name);
    private sealed class NodeKeyComparer : IEqualityComparer<NodeKey>
    {
        public bool Equals(NodeKey a, NodeKey b) => a.Parent == b.Parent && StringComparer.OrdinalIgnoreCase.Equals(a.Name, b.Name);
        public int GetHashCode(NodeKey key) => HashCode.Combine(key.Parent, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
    }
    private sealed class SnapshotLookup
    {
        internal readonly ScanResult Result;
        private readonly Dictionary<NodeKey, int> children;
        private readonly int[] nextName;
        internal SnapshotLookup(ScanResult result, CancellationToken cancellationToken)
        {
            Result = result;
            children = new Dictionary<NodeKey, int>(result.Count, new NodeKeyComparer());
            nextName = new int[result.Count]; Array.Fill(nextName, -1);
            for (int index = 1; index < result.Count; index++)
            {
                if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var node = result.Nodes[index];
                var key = new NodeKey(node.ParentIndex, node.Name);
                if (children.TryGetValue(key, out int existing)) nextName[index] = existing;
                children[key] = index;
            }
        }
        internal int Resolve(string path, string target)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(path, target)) return 0;
            int cursor = target.Length, parent = 0;
            while (cursor < path.Length)
            {
                if ((Result.Nodes[parent].Flags & NodeFlags.ReparseSkipped) != 0) return -1;
                while (cursor < path.Length && (path[cursor] == Path.DirectorySeparatorChar || path[cursor] == Path.AltDirectorySeparatorChar)) cursor++;
                if (cursor == path.Length) return parent;
                int end = path.IndexOf(Path.DirectorySeparatorChar, cursor);
                if (end < 0) end = path.Length;
                string name = path[cursor..end];
                if (!children.TryGetValue(new(parent, name), out int first)) return -1;
                int match = -1;
                for (int index = first; index >= 0; index = nextName[index])
                    if (StringComparer.Ordinal.Equals(Result.Nodes[index].Name, name)) { match = index; break; }
                if (match < 0)
                {
                    // Case-sensitive directories can contain case-only sibling names.
                    // Fall back case-insensitively only when the lookup is unambiguous.
                    if (nextName[first] >= 0) return -1;
                    match = first;
                }
                parent = match; cursor = end;
            }
            return parent;
        }
    }
}
