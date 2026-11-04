using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace DiskPeek.Core;

public sealed class DirectoryScanEngine : IScanEngine
{
    public string Name => "Directory";

    public async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        string target = Path.GetFullPath(request.Target);
        var attributes = NativeDirectory.RootAttributes(target);
        if ((attributes & FileAttributes.Directory) == 0) throw new ArgumentException("Choose a folder or drive.");
        var timer = Stopwatch.StartNew();
        var nodes = new List<ScanNode>(65536) { new() { Name = target, ParentIndex = -1,
            Attributes = attributes, AllocatedBytes = 0 } };
        var issues = new List<ScanIssue>();
        object gate = new();
        long skipped = 0, directories = 1, published = 0;
        bool partial = false, stableIds = true;
        int workerCount = request.WorkerCount > 0 ? Math.Clamp(request.WorkerCount, 1, 16) : NativeDirectory.RecommendedWorkers(target);
        // Queue only indices into the snapshot, rather than retaining full paths for every pending directory.
        var queue = new ConcurrentQueue<int>();
        using var available = new SemaphoreSlim(0);
        long outstanding = 1;

        void AddIssue(string path, string message)
        {
            partial = true;
            skipped++;
            if (issues.Count < 1000) issues.Add(new(path, message));
        }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
        {
            var root = nodes[0]; root.AllocatedBytes = -1; root.Flags |= NodeFlags.ReparseSkipped; nodes[0] = root;
            AddIssue(target, "The selected folder is a reparse point. Choose its target folder to scan it.");
            outstanding = 0;
        }
        else { queue.Enqueue(0); available.Release(); }

        string PathFor(int index)
        {
            lock (gate)
            {
                var parts = new List<string>();
                while (index > 0) { parts.Add(nodes[index].Name); index = nodes[index].ParentIndex; }
                parts.Reverse();
                return parts.Count == 0 ? target : Path.Combine([target, .. parts]);
            }
        }

        void Report(bool final = false)
        {
            long now = timer.ElapsedMilliseconds;
            long previous = Interlocked.Read(ref published);
            if (!final && (now - previous < 150 || Interlocked.CompareExchange(ref published, now, previous) != previous)) return;
            if (final) Interlocked.Exchange(ref published, now);
            ScanProgress value;
            lock (gate) value = new(nodes.Count - 1, directories, timer.Elapsed,
                final ? cancellationToken.IsCancellationRequested ? "Cancelled" : "Complete" : "Enumerating", Name);
            progress?.Report(value);
        }

        async Task RunWorker()
        {
            while (Volatile.Read(ref outstanding) > 0 && !cancellationToken.IsCancellationRequested)
            {
                try { await available.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (!queue.TryDequeue(out int parent)) continue;
                string path = PathFor(parent);
                try
                {
                    NativeDirectory.Read(path, cancellationToken, batch =>
                    {
                        lock (gate)
                        {
                            foreach (var entry in batch)
                            {
                                int index = nodes.Count;
                                nodes.Add(new() { Name = entry.Name, ParentIndex = parent, Identity = entry.Identity,
                                    LogicalBytes = entry.Logical, AllocatedBytes = entry.Allocated,
                                    ModifiedUtcTicks = entry.ModifiedUtcTicks, Attributes = entry.Attributes, Flags = entry.Flags });
                                string? childPath = null;
                                if (!entry.Identity.IsKnown && (entry.Attributes & FileAttributes.Directory) == 0) stableIds = false;
                                if (entry.MetadataError != 0)
                                    AddIssue(Path.Combine(path, entry.Name), "Allocation unavailable: " + new Win32Exception(entry.MetadataError).Message);
                                if ((entry.Attributes & FileAttributes.Directory) == 0) continue;
                                directories++;
                                childPath = Path.Combine(path, entry.Name);
                                if ((entry.Flags & NodeFlags.ReparseSkipped) != 0)
                                { AddIssue(childPath, "Reparse or offline directory not traversed."); continue; }
                                Interlocked.Increment(ref outstanding);
                                queue.Enqueue(index);
                                available.Release();
                            }
                        }
                        Report();
                    });
                }
                catch (OperationCanceledException)
                {
                    lock (gate)
                    {
                        var node = nodes[parent]; node.Flags |= NodeFlags.Inaccessible; node.AllocatedBytes = -1;
                        nodes[parent] = node;
                        AddIssue(path, "Scan cancelled before this directory finished.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    lock (gate)
                    {
                        var node = nodes[parent]; node.Flags |= NodeFlags.Inaccessible; node.AllocatedBytes = -1;
                        nodes[parent] = node;
                        AddIssue(path, ex.Message);
                    }
                }
                finally
                {
                    if (Interlocked.Decrement(ref outstanding) == 0) available.Release(workerCount);
                }
            }
        }

        // Native enumeration is synchronous, so all of it stays off the caller/UI thread.
        await Task.WhenAll(Enumerable.Range(0, workerCount).Select(_ => Task.Run(RunWorker))).ConfigureAwait(false);
        bool cancelled = cancellationToken.IsCancellationRequested;
        if (cancelled)
            while (queue.TryDequeue(out var pending))
            {
                var node = nodes[pending]; node.Flags |= NodeFlags.Inaccessible; node.AllocatedBytes = -1;
                nodes[pending] = node;
                AddIssue(PathFor(pending), "Scan cancelled before this directory was visited.");
            }
        long? capacity = null, free = null;
        try { var drive = new DriveInfo(Path.GetPathRoot(target)!); capacity = drive.TotalSize; free = drive.TotalFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        Report(true);
        return new() { Target = target, Nodes = nodes.ToArray(), Engine = Name, Elapsed = timer.Elapsed,
            CompletedUtc = DateTime.UtcNow, Cancelled = cancelled, Partial = partial || cancelled,
            HardLinkAccountingAvailable = stableIds, Issues = issues, SkippedEntries = skipped,
            VolumeCapacity = capacity, VolumeFree = free };
    }
}
