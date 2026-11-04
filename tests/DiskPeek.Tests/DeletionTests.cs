using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskPeek.Core;

internal static partial class Verification
{
    private static async Task RunDeletionTests()
    {
        await Run("Deletion tracking: only missing errors mark monotonic snapshot tombstones", TestDeletionErrorClassification);
        await Run("Deletion tracking: buffered scan/attach gap and bounded queue overflow", TestDeletionAttachGap);
        await Run("Deletion tracking: precise parent/name lookup and outside-scope boundaries", TestDeletionLookupBoundaries);
        await Run("Deletion tracking: large grouped subtree counts and duplicate coalescing", TestDeletionLargeSubtree);
        await Run("Deletion tracking: disconnected drive and watcher errors stay incomplete", TestDeletionAvailability);
        await Run("Deletion tracking: disposal stops callbacks and cancelled attach stops indexing", TestDeletionLifetime);
        await Run("Deletion tracking: real watcher file/folder deletion preserves scan sizes", TestRealDeletionWatcher);
        await Run("Deletion tracking: real rename/move and scan-attach gap", TestRealDeletionRenameGap);
        await Run("Recycle Bin: roots, paths, unknown identities and placeholders are rejected", TestRecycleSelectionGuards);
        await Run("Recycle Bin: stale item/ancestor identities and junction swaps are rejected", TestRecycleChangedPaths);
        await Run("Recycle Bin: pre-cancel and locked-item failure preserve fixture data", TestRecycleCancellationAndFailure);
        await Run("Recycle Bin: actual file and folder recycling updates tracked snapshot", TestActualRecycleBin);
    }

    private static ScanResult DeletionModel(string target, params ScanNode[] nodes) => ScanAggregation.Complete(new()
    { Target = Path.GetFullPath(target), Engine = "Deletion fixture", Nodes = nodes });

    private static async Task TestDeletionErrorClassification()
    {
        var root = NewFixture("deletion-errors");
        var errors = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = DeletionModel(root, Dir("root", -1), FileNode("file.bin", 0, 123, 4096, 1));
        var file = result.GetPath(1);
        await using var monitor = new ScanDeletionMonitor(root, p => errors.GetValueOrDefault(p));
        await monitor.AttachAsync(result);
        foreach (int error in new[] { 0, 5, 21, 32, 53, 64, 123, 995 })
        {
            errors[file] = error;
            monitor.NotifyPath(file);
            await monitor.FlushAsync();
            Assert(!monitor.State.IsDeleted(1), $"Win32 error {error} was misclassified as deletion.");
        }
        errors[file] = 2;
        monitor.NotifyPath(file);
        await monitor.FlushAsync();
        Assert(monitor.State.IsDeleted(1) && monitor.State.DeletedCount == 1 && monitor.State.GetDeletedCount(0) == 1, "Confirmed absence not reflected in snapshot counters.");
        Assert(monitor.State.HasDeletedDescendants(0) && !monitor.State.HasDeletedDescendants(1), "Strict descendant count incorrect.");
        errors[file] = 0;
        monitor.NotifyPath(file);
        await monitor.FlushAsync();
        Assert(monitor.State.IsDeleted(1) && result.AllocatedTotals[0] == 4096 && result.LogicalTotals[0] == 123, "A recreated path cleared a tombstone or changed historical scan totals.");
        Assert(!monitor.State.IsDeleted(-1) && monitor.State.GetDeletedCount(99) == 0, "Out-of-range snapshot reads must be safe.");
        var second = DeletionModel(root, Dir("root", -1), FileNode("path-missing.bin", 0, 9, 0, 2));
        errors[second.GetPath(1)] = 3;
        await using var other = new ScanDeletionMonitor(root, p => errors.GetValueOrDefault(p));
        await other.AttachAsync(second);
        other.NotifyPath(second.GetPath(1));
        await other.FlushAsync();
        Assert(other.State.IsDeleted(1), "Win32 path-not-found did not mark the snapshot path.");
    }

    private static async Task TestDeletionAttachGap()
    {
        var root = NewFixture("deletion-attach-gap");
        var result = DeletionModel(root, Dir("root", -1), FileNode("gap.bin", 0, 1, 0, 1));
        string file = result.GetPath(1);
        await using (var monitor = new ScanDeletionMonitor(root, p => SamePath(p, file) ? 2 : 0))
        {
            monitor.NotifyPath(file); // Notification arrived after enumeration but before attaching its result.
            await monitor.AttachAsync(result);
            Assert(monitor.State.IsDeleted(1), "Attaching dropped a deletion buffered while the scan was running.");
            try { await monitor.AttachAsync(result); throw new Exception("A monitor accepted a second snapshot."); }
            catch (InvalidOperationException) { }
        }
        var nodes = new[] { Dir("root", -1), FileNode("a", 0, 1, 0, 1), FileNode("b", 0, 1, 0, 2), FileNode("c", 0, 1, 0, 3) };
        var larger = DeletionModel(root, nodes);
        await using var bounded = new ScanDeletionMonitor(root, p => SamePath(p, Path.GetPathRoot(root)!) || SamePath(p, root) ? 0 : 2, queueCapacity: 1);
        bounded.NotifyPath(larger.GetPath(1)); bounded.NotifyPath(larger.GetPath(2)); bounded.NotifyPath(larger.GetPath(3));
        await bounded.AttachAsync(larger);
        Assert(bounded.State.TrackingIncomplete && !string.IsNullOrWhiteSpace(bounded.State.Status), "Overflow was silently presented as complete tracking.");
        bounded.NotifyPath(larger.GetPath(3));
        await bounded.FlushAsync();
        Assert(bounded.State.IsDeleted(3) && bounded.State.TrackingIncomplete, "Tracking did not retain confirmed deletions after a queue overflow.");
    }

    private static async Task TestDeletionLookupBoundaries()
    {
        var root = NewFixture("deletion-boundaries");
        var errors = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = DeletionModel(root, Dir("root", -1), Dir("a", 0), FileNode("same.txt", 1, 3, 0, 1), Dir("b", 0), FileNode("same.txt", 3, 4, 0, 2));
        int outsideProbes = 0;
        var outside = root + "-sibling\\same.txt";
        await using var monitor = new ScanDeletionMonitor(root, p => { if (SamePath(p, outside)) Interlocked.Increment(ref outsideProbes); return errors.GetValueOrDefault(p); });
        await monitor.AttachAsync(result);
        errors[result.GetPath(2)] = 2;
        monitor.NotifyPath(result.GetPath(2));
        monitor.NotifyPath(outside);
        monitor.NotifyPath(Path.Combine(root, "..", Path.GetFileName(root) + "-sibling", "same.txt"));
        await monitor.FlushAsync();
        Assert(monitor.State.IsDeleted(2) && !monitor.State.IsDeleted(4), "The same leaf name in another parent was marked deleted.");
        Assert(outsideProbes == 0 && monitor.State.DeletedCount == 1, "An outside path passed the scope boundary.");
        var mismatch = DeletionModel(root + "-other", Dir("root", -1));
        await using var unattached = new ScanDeletionMonitor(root, _ => 0);
        try { await unattached.AttachAsync(mismatch); throw new Exception("A different target snapshot was accepted."); }
        catch (ArgumentException) { }
    }

    private static async Task TestDeletionLargeSubtree()
    {
        const int files = 100_000;
        var root = NewFixture("deletion-large");
        var nodes = new ScanNode[files + 2]; nodes[0] = Dir("root", -1); nodes[1] = Dir("group", 0);
        for (int i = 0; i < files; i++) nodes[i + 2] = FileNode($"entry-{i:D6}.bin", 1, 64, 0, (ulong)i + 1);
        var result = DeletionModel(root, nodes);
        var errors = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int probes = 0, publications = 0;
        await using var monitor = new ScanDeletionMonitor(root, p => { Interlocked.Increment(ref probes); return errors.GetValueOrDefault(p); });
        monitor.Changed += (_, _) => Interlocked.Increment(ref publications);
        await monitor.AttachAsync(result);
        Interlocked.Exchange(ref probes, 0); Interlocked.Exchange(ref publications, 0);
        var last = result.GetPath(files + 1);
        errors[last] = 2;
        for (int i = 0; i < 64; i++) monitor.NotifyPath(last);
        await monitor.FlushAsync();
        Assert(monitor.State.IsDeleted(files + 1) && monitor.State.DeletedCount == 1 && monitor.State.GetDeletedCount(1) == 1, "Large-group individual tombstone/count incorrect.");
        Assert(probes <= 16, $"One coalesced name notification triggered {probes:N0} metadata probes.");
        Assert(publications <= 3, "Duplicate events were published without coalescing.");
        errors[result.GetPath(1)] = 3;
        monitor.NotifyPath(result.GetPath(1));
        await monitor.FlushAsync();
        Assert(monitor.State.IsDeleted(1) && monitor.State.GetDeletedCount(1) == files + 1 && monitor.State.DeletedCount == files + 1, "Deleting a grouped folder omitted or double-counted descendants.");
        Assert(!monitor.State.IsDeleted(0) && monitor.State.GetDeletedCount(0) == files + 1 && result.FileCounts[0] == files, "Deleting a child group altered the root identity or immutable scan counts.");
    }

    private static async Task TestDeletionAvailability()
    {
        var root = NewFixture("deletion-availability");
        var result = DeletionModel(root, Dir("root", -1), FileNode("file.bin", 0, 1, 0, 1));
        int volumeError = 21;
        await using var monitor = new ScanDeletionMonitor(root, p => SamePath(p, Path.GetPathRoot(root)!) ? Volatile.Read(ref volumeError) : SamePath(p, result.GetPath(1)) ? 2 : 0);
        await monitor.AttachAsync(result);
        monitor.NotifyPath(result.GetPath(1));
        await monitor.FlushAsync();
        Assert(monitor.State.DeletedCount == 0 && monitor.State.TrackingIncomplete, "Unavailable media was rendered as deleted data.");
        Interlocked.Exchange(ref volumeError, 0);
        monitor.ReportWatcherError(new InternalBufferOverflowException());
        monitor.NotifyPath(result.GetPath(1));
        await monitor.FlushAsync();
        Assert(monitor.State.IsDeleted(1) && monitor.State.TrackingIncomplete && monitor.State.Status.Contains("Rescan", StringComparison.OrdinalIgnoreCase), "Overflow health/confirmed deletion was lost.");
    }

    private static async Task TestDeletionLifetime()
    {
        var root = NewFixture("deletion-lifetime");
        var result = DeletionModel(root, Dir("root", -1), FileNode("file.bin", 0, 1, 0, 1));
        int probes = 0, events = 0;
        var monitor = new ScanDeletionMonitor(root, _ => { Interlocked.Increment(ref probes); return 0; });
        monitor.Changed += (_, _) => Interlocked.Increment(ref events);
        await monitor.AttachAsync(result);
        await monitor.DisposeAsync();
        int beforeProbes = probes, beforeEvents = events;
        monitor.NotifyPath(result.GetPath(1));
        await Task.Delay(150);
        Assert(probes == beforeProbes && events == beforeEvents && monitor.State.DeletedCount == 0, "Disposed monitor still processed or published changes.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await using var other = new ScanDeletionMonitor(root, _ => 0);
        try { await other.AttachAsync(result, cancelled.Token); throw new Exception("Cancelled attachment completed."); }
        catch (OperationCanceledException) { }
    }

    private static async Task TestRealDeletionWatcher()
    {
        var root = NewFixture("deletion-real");
        var folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder);
        var child = Path.Combine(folder, "child.bin"); await System.IO.File.WriteAllBytesAsync(child, new byte[8193]);
        var individual = Path.Combine(root, "résumé,世界.txt"); await System.IO.File.WriteAllTextAsync(individual, "watch me");
        await using var monitor = new ScanDeletionMonitor(root);
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
        await monitor.AttachAsync(result);
        RequireWatcher(monitor);
        int fileIndex = IndexOf(result, individual), folderIndex = IndexOf(result, folder), childIndex = IndexOf(result, child);
        long logical = result.LogicalTotals[0], allocated = result.AllocatedTotals[0]; int count = result.Count;
        System.IO.File.Delete(individual);
        await WaitUntil(() => monitor.State.IsDeleted(fileIndex), "FileSystemWatcher deletion never reached the snapshot.");
        await System.IO.File.WriteAllTextAsync(individual, "new replacement");
        await Task.Delay(150);
        Assert(monitor.State.IsDeleted(fileIndex), "Recreation cleared a confirmed snapshot tombstone.");
        Directory.Delete(folder, recursive: true); // Only the newly created fixture subtree.
        await WaitUntil(() => monitor.State.IsDeleted(folderIndex) && monitor.State.IsDeleted(childIndex), "Deleted folder descendants were not marked.");
        Assert(monitor.State.GetDeletedCount(folderIndex) == 2 && monitor.State.DeletedCount == 3, "Real watcher subtree counts incorrect.");
        Assert(result.Count == count && result.LogicalTotals[0] == logical && result.AllocatedTotals[0] == allocated, "Live deletion changed the saved scan's areas or totals.");
    }

    private static async Task TestRealDeletionRenameGap()
    {
        var root = NewFixture("deletion-rename");
        var file = Path.Combine(root, "old.bin"); await System.IO.File.WriteAllTextAsync(file, "rename");
        var gap = Path.Combine(root, "gap.bin"); await System.IO.File.WriteAllTextAsync(gap, "gap");
        var folder = Path.Combine(root, "move-me"); Directory.CreateDirectory(folder);
        var child = Path.Combine(folder, "child.txt"); await System.IO.File.WriteAllTextAsync(child, "move");
        await using var monitor = new ScanDeletionMonitor(root);
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
        System.IO.File.Delete(gap); // A real change after scan enumeration, before attaching the result.
        await monitor.AttachAsync(result);
        RequireWatcher(monitor);
        await WaitUntil(() => monitor.State.IsDeleted(IndexOf(result, gap)), "A real scan/attach-gap deletion was dropped.");
        System.IO.File.Move(file, Path.Combine(root, "new.bin"));
        await WaitUntil(() => monitor.State.IsDeleted(IndexOf(result, file)), "Renaming did not mark the original snapshot path.");
        var outside = NewFixture("deletion-move-destination");
        Directory.Move(folder, Path.Combine(outside, "moved"));
        await WaitUntil(() => monitor.State.IsDeleted(IndexOf(result, folder)) && monitor.State.IsDeleted(IndexOf(result, child)), "Moving a folder out of scope did not mark its old subtree.");
        Assert(monitor.State.DeletedCount == 4 && result.Count == 5, "Moves were added as new scan entries or old paths were double counted.");
    }

    private static async Task TestRecycleSelectionGuards()
    {
        var root = NewFixture("recycle-guards");
        var file = Path.Combine(root, "keep.txt"); await System.IO.File.WriteAllTextAsync(file, "must stay");
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
        int index = IndexOf(result, file);
        Assert(RecycleBinService.ValidateSelection(result, index).Success, "A fresh normal fixture file was not eligible.");
        foreach (int invalid in new[] { -1, 0, result.Count }) Assert(!RecycleBinService.ValidateSelection(result, invalid).Success, "Scan root or invalid selection accepted.");
        var original = result.Nodes[index];
        var unknown = original; unknown.Identity = default; result.Nodes[index] = unknown;
        Assert(!RecycleBinService.ValidateSelection(result, index).Success, "An unknown identity was eligible for deletion.");
        foreach (var attributes in new[] { FileAttributes.ReparsePoint, FileAttributes.Offline })
        {
            var changed = original; changed.Attributes |= attributes; result.Nodes[index] = changed;
            Assert(!RecycleBinService.ValidateSelection(result, index).Success, "A link/offline placeholder was eligible for deletion.");
        }
        foreach (var name in new[] { ".", "..", "..\\outside.txt", root + "-sibling\\outside.txt", @"C:\", "$Recycle.Bin", "ambiguous.", "ambiguous " })
        {
            var changed = original; changed.Name = name; result.Nodes[index] = changed;
            Assert(!RecycleBinService.ValidateSelection(result, index).Success, "A protected/out-of-scope path was eligible: " + name);
        }
        result.Nodes[index] = original;
        var network = new ScanResult { Target = @"\\server\share", Engine = "guard", Nodes = [Dir("root", -1), FileNode("file.txt", 0, 1, 1, 1)] };
        Assert(!RecycleBinService.ValidateSelection(network, 1).Success, "Network path was eligible for in-app recycling.");
        Assert(System.IO.File.Exists(file), "Structural guard checks modified the fixture.");
    }

    private static async Task TestRecycleChangedPaths()
    {
        var root = NewFixture("recycle-changed");
        var file = Path.Combine(root, "replace.txt"); await System.IO.File.WriteAllTextAsync(file, "old identity");
        var scanned = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
        var held = Path.Combine(root, "original-held.txt"); System.IO.File.Move(file, held);
        await System.IO.File.WriteAllTextAsync(file, "new identity");
        var changed = await RecycleBinService.MoveAsync(scanned, IndexOf(scanned, file));
        Assert(!changed.Success && System.IO.File.Exists(file) && System.IO.File.Exists(held), "A replacement file was recycled using a stale identity.");
        var parent = Path.Combine(root, "parent"); Directory.CreateDirectory(parent);
        var inside = Path.Combine(parent, "child.txt"); await System.IO.File.WriteAllTextAsync(inside, "child");
        var second = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
        var previous = Path.Combine(root, "parent-held"); Directory.Move(parent, previous); Directory.CreateDirectory(parent);
        System.IO.File.Move(Path.Combine(previous, "child.txt"), inside); // Same leaf identity, different parent identity.
        var replacedParent = await RecycleBinService.MoveAsync(second, IndexOf(second, inside));
        Assert(!replacedParent.Success && System.IO.File.Exists(inside), "A changed ancestor identity was not rejected.");
        Directory.Move(parent, Path.Combine(root, "replacement-held"));
        Directory.CreateDirectory(parent);
        var destination = NewFixture("recycle-junction-destination");
        var canary = Path.Combine(destination, "child.txt"); await System.IO.File.WriteAllTextAsync(canary, "outside canary");
        MakeJunction(parent, destination);
        var junctionResult = await RecycleBinService.MoveAsync(second, IndexOf(second, inside));
        Assert(!junctionResult.Success && System.IO.File.Exists(canary), "A junction swapped into the selected path was traversed for recycling.");
    }

    private static async Task TestRecycleCancellationAndFailure()
    {
        var root = NewFixture("recycle-cancel");
        var file = Path.Combine(root, "keep.txt"); await System.IO.File.WriteAllTextAsync(file, "locked fixture must remain");
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
        int index = IndexOf(result, file);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = await RecycleBinService.MoveAsync(result, index, cancel.Token);
        Assert(!cancelled.Success && cancelled.Cancelled && System.IO.File.Exists(file), "Pre-cancelled recycling changed a file.");
        using (var held = System.IO.File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await RecycleBinService.MoveAsync(result, index);
            Assert(!failure.Success && System.IO.File.Exists(file), "Locked-item failure fell back to permanent deletion.");
        }
        System.IO.File.Delete(file); // A fresh test-only file; missing selections must fail safely.
        var missing = await RecycleBinService.MoveAsync(result, index);
        Assert(!missing.Success && !missing.Cancelled, "An already missing item was reported as recycled.");
    }

    private static async Task TestActualRecycleBin()
    {
        var root = NewFixture("recycle-native");
        var file = Path.Combine(root, "diskpeek-fresh-recycle-" + Guid.NewGuid().ToString("N") + ".txt");
        await System.IO.File.WriteAllTextAsync(file, "Fresh DiskPeek verification fixture, safe to recycle.");
        var folder = Path.Combine(root, "diskpeek-fresh-folder-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var child = Path.Combine(folder, "child.txt"); await System.IO.File.WriteAllTextAsync(child, "Fresh child fixture.");
        var before = QueryRecycleBin(root);
        await using var monitor = new ScanDeletionMonitor(root);
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
        await monitor.AttachAsync(result);
        int fileIndex = IndexOf(result, file), folderIndex = IndexOf(result, folder), childIndex = IndexOf(result, child);
        var recycled = await RecycleBinService.MoveAsync(result, fileIndex);
        Assert(recycled.Success && !recycled.Cancelled && !System.IO.File.Exists(file), "Windows did not recycle the fresh file: " + recycled.Message);
        var recycledFolder = await RecycleBinService.MoveAsync(result, folderIndex);
        Assert(recycledFolder.Success && !recycledFolder.Cancelled && !Directory.Exists(folder), "Windows did not recycle the fresh folder: " + recycledFolder.Message);
        var after = QueryRecycleBin(root);
        Assert(after.Items >= before.Items + 2 && after.Bytes >= before.Bytes, "Recycle Bin item accounting did not grow after successful file/folder recycling.");
        monitor.NotifyPath(file); monitor.NotifyPath(folder); // App uses the same confirmation path after shell success.
        await monitor.FlushAsync();
        Assert(monitor.State.IsDeleted(fileIndex) && monitor.State.IsDeleted(folderIndex) && monitor.State.IsDeleted(childIndex), "Successful recycling did not mark the historical snapshot paths.");
        Assert(result.FileCounts[0] == 2 && result.LogicalTotals[0] > 0, "Recycling rewrote historical scan counts or totals.");
    }

    private static int IndexOf(ScanResult result, string path) => Enumerable.Range(0, result.Count).Single(i => SamePath(result.GetPath(i), path));
    private static bool SamePath(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));
    private static void RequireWatcher(ScanDeletionMonitor monitor)
    { if (monitor.State.TrackingIncomplete) throw new Skipped("Filesystem watcher unavailable: " + monitor.State.Status); }
    private static async Task WaitUntil(Func<bool> predicate, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.Elapsed < TimeSpan.FromSeconds(6)) await Task.Delay(25);
        Assert(predicate(), message);
    }
    private static (long Bytes, long Items) QueryRecycleBin(string fixture)
    {
        var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        int status = SHQueryRecycleBinW(Path.GetPathRoot(fixture), ref info);
        if (status < 0) throw new Skipped($"The OS Recycle Bin is unavailable for this fixture volume (HRESULT 0x{status:X8}).");
        return (info.Bytes, info.Items);
    }
    [StructLayout(LayoutKind.Sequential)] private struct RecycleBinInfo { public uint Size; public long Bytes, Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBinW(string? root, ref RecycleBinInfo info);
}
