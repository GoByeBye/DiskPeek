using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DiskPeek.Core;
using Microsoft.Win32.SafeHandles;

return await Verification.RunAsync(args);

internal static partial class Verification
{
    private static readonly List<TestOutcome> Outcomes = [];
    private static string FixtureRoot = "";
    private static string ReportPath = "";

    public static async Task<int> RunAsync(string[] args)
    {
        var workspace = Path.GetFullPath("work");
        Directory.CreateDirectory(workspace);
        FixtureRoot = Path.Combine(workspace, "verification-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FixtureRoot);
        ReportPath = args.Length == 2 && args[0] == "--report" ? Path.GetFullPath(args[1]) : Path.Combine(workspace, "verification-results.json");
        await Run("Aggregation: nested and out-of-order parent records", TestAggregation);
        await Run("Hard links: deterministic scoped allocation", TestHardLinks);
        await Run("Unknown allocation and skipped subtrees stay incomplete", TestUnknown);
        await Run("Cyclic parent references are rejected", TestCycle);
        await Run("CSV: Unicode, quoting, statuses, and unknown values", TestCsv);
        await Run("MFT: valid resident FILE record and names", TestMftResident);
        await Run("MFT: 4Kn volume records use 512-byte fixup stride", TestMft4Kn);
        await Run("MFT: nonresident sparse and fragmented runs", TestMftRuns);
        await Run("MFT: malformed record boundaries and fixups", TestMftMalformed);
        await Run("MFT: attribute continuation references", TestAttributeList);
        await Run("MFT: deterministic malformed-input corpus", TestParserCorpus);
        await Run("MFT direct I/O: unaligned and cross-chunk reads", TestAlignedReads);
        await Run("Worker: rejects folders and non-volume device paths", TestWorkerTargets);
        await Run("Worker protocol: roundtrip metadata and completion", TestProtocolRoundtrip);
        await Run("Worker protocol: rejects lengths, truncation and mismatched targets", TestProtocolMalformed);
        await Run("Updates: checking only notifies; applying requires consent and idle state", TestUpdateConsent);
        await Run("Updates: portable mode never checks, downloads or applies", TestPortableUpdates);
        await Run("Updates: stable newer versions only; rejected updates clear readiness", TestUpdateVersions);
        await Run("Updates: staged payload size and SHA-256 are verified, including cached corruption", TestUpdatePayload);
        await Run("Updates: cancellation and offline failures preserve safe state", TestUpdateFailures);
        await Run("Updates: an active operation blocks apply and queued cancellation", TestUpdateConcurrency);
        await Run("Updates: background checks honor opt-out, daily interval and clock drift", TestUpdateSchedule);
        await Run("Settings: theme/update opt-out roundtrip and malformed recovery", TestUserSettings);
        await Run("Filesystem: empty, deep, Unicode and long paths", TestRealDirectory);
        await Run("Filesystem: hard-link accounting matches baseline", TestRealHardLinks);
        await Run("Filesystem: sparse allocation matches Windows", TestSparse);
        await Run("Filesystem: NTFS compression matches Windows", TestCompressed);
        await Run("Filesystem: alternate streams excluded consistently", TestAlternateStream);
        await Run("Filesystem: directory reparse loops are not traversed", TestReparseLoop);
        await Run("Filesystem: denied directory reports partial", TestDenied);
        await Run("Filesystem: locked content does not prevent metadata scan", TestLocked);
        await Run("Filesystem: cancellation keeps partial results", TestCancellation);
        await Run("Filesystem: changing directory does not crash", TestChanging);
        await RunDeletionTests();
        var report = new
        {
            GeneratedUtc = DateTime.UtcNow, FixtureRoot, Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString(),
            Passed = Outcomes.Count(t => t.Status == "passed"), Failed = Outcomes.Count(t => t.Status == "failed"), Skipped = Outcomes.Count(t => t.Status == "skipped"),
            Outcomes,
            UntestedExternalScenarios = new[] { "Cloud provider hydration behavior requires a configured cloud placeholder fixture.", "Raw-volume engine equivalence requires an elevated controlled NTFS test volume; parser paths are tested here.", "UI, UAC cancellation, and worker process failure require GUI integration checks." }
        };
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
        await File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\n{report.Passed} passed; {report.Failed} failed; {report.Skipped} skipped. Report: {ReportPath}\nFixtures retained: {FixtureRoot}");
        return report.Failed == 0 ? 0 : 1;
    }

    private static async Task Run(string name, Func<Task> test)
    {
        var clock = Stopwatch.StartNew();
        try { await test(); Outcomes.Add(new(name, "passed", clock.Elapsed.TotalMilliseconds, null)); Console.WriteLine("PASS " + name); }
        catch (Skipped ex) { Outcomes.Add(new(name, "skipped", clock.Elapsed.TotalMilliseconds, ex.Message)); Console.WriteLine("SKIP " + name + ": " + ex.Message); }
        catch (Exception ex) { Outcomes.Add(new(name, "failed", clock.Elapsed.TotalMilliseconds, ex.ToString())); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Task Done() => Task.CompletedTask;
    private static ScanNode Dir(string name, int parent) => new() { Name = name, ParentIndex = parent, Attributes = FileAttributes.Directory, AllocatedBytes = 0 };
    private static ScanNode FileNode(string name, int parent, long logical, long allocation, ulong id) => new() { Name = name, ParentIndex = parent, LogicalBytes = logical, AllocatedBytes = allocation, Identity = new(id) };
    private static ScanResult Model(params ScanNode[] nodes) => ScanAggregation.Complete(new() { Target = @"C:\fixture", Nodes = nodes, Engine = "test" });

    private static Task TestAggregation()
    {
        var result = Model(Dir("root", -1), FileNode("nested.bin", 2, 12, 4096, 101), Dir("nested", 0), FileNode("resident.txt", 0, 5, 0, 102));
        Assert(result.LogicalTotals[0] == 17, "Logical totals differ.");
        Assert(result.AllocatedTotals[0] == 4096, "Allocated totals differ.");
        Assert(result.FileCounts[0] == 2 && result.DirectoryCounts[0] == 1, "Counts differ.");
        Assert(result.Children(0).Count() == 2 && result.GetPath(1) == @"C:\fixture\nested\nested.bin", "Adjacency or lazy path incorrect.");
        Assert(result.IsDescendantOf(1, 2) && !result.IsDescendantOf(3, 2), "Descendant test incorrect.");
        return Done();
    }
    private static Task TestHardLinks()
    {
        var result = Model(Dir("root", -1), Dir("z", 0), FileNode("linked.bin", 1, 100, 4096, 77), Dir("a", 0), FileNode("linked.bin", 3, 100, 4096, 77));
        Assert(result.LogicalTotals[0] == 200 && result.AllocatedTotals[0] == 4096, "Shared storage double counted.");
        Assert((result.Nodes[2].Flags & NodeFlags.SharedAllocation) != 0 && (result.Nodes[4].Flags & NodeFlags.SharedAllocation) == 0, "Ownership depends on discovery order.");
        Assert(result.AllocatedTotals[1] == 0 && result.AllocatedTotals[3] == 4096, "Scoped totals not attributed to canonical in-scope path.");
        var oneLink = Model(Dir("root", -1), FileNode("linked.bin", 0, 100, 4096, 77));
        Assert(oneLink.AllocatedTotals[0] == 4096, "Link outside scan scope took ownership.");
        return Done();
    }
    private static Task TestUnknown()
    {
        var denied = Dir("denied", 0); denied.Flags = NodeFlags.Inaccessible;
        var result = Model(Dir("root", -1), FileNode("unknown.bin", 0, 9000, -1, 1), denied, FileNode("measured.bin", 0, 200, 4096, 2));
        Assert(result.AllocatedTotals[0] == 4096 && result.LogicalTotals[0] == 9200, "Unknown allocation substituted logical bytes.");
        Assert(result.UnknownCounts[0] == 2, "Unknown subtree coverage missing.");
        return Done();
    }
    private static Task TestCycle()
    {
        var result = new ScanResult { Target = @"C:\fixture", Engine = "test", Nodes = [Dir("root", -1), Dir("a", 2), Dir("b", 1)] };
        try { ScanAggregation.Complete(result); throw new Exception("Cycle was accepted."); }
        catch (InvalidDataException) { }
        return Done();
    }
    private static async Task TestCsv()
    {
        var path = Path.Combine(FixtureRoot, "export.csv");
        var result = Model(Dir("root", -1), FileNode("résumé,世界.txt", 0, 123, -1, 1));
        await CsvExporter.ExportAsync(result, path);
        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        var csv = await System.IO.File.ReadAllTextAsync(path);
        Assert(bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM missing.");
        Assert(csv.Contains("résumé,世界.txt\",\"File\",\"123\",\"\",\"False\""), "CSV name quoting or unknown allocation incorrect.");
        Assert(csv.Contains("Unnamed file data only"), "Accounting scope omitted.");
    }

    private static Task TestMftResident()
    {
        var bytes = MakeRecord();
        Assert(MftParser.TryParseRecord(bytes, 512, out var record, out var error), "Valid FILE rejected: " + error);
        Assert(record.RecordNumber == 123 && record.Sequence == 7 && record.Reference == (123UL | 7UL << 48), "Sequence-aware reference incorrect.");
        Assert(record.Names.Single().Name == "測試.txt" && record.Names.Single().ParentReference == (5UL | 3UL << 48), "FILE_NAME incorrect.");
        Assert(record.DataStreams.Single().Resident && record.DataStreams.Single().LogicalSize == 3, "Resident stream size incorrect.");
        return Done();
    }
    private static Task TestMftRuns()
    {
        byte[] bytes = [0x11, 3, 100, 0x01, 2, 0x11, 4, 0xF6, 0];
        var runs = MftParser.DecodeRunList(bytes);
        Assert(runs.Count == 3, "Run count incorrect.");
        Assert(runs[0] == new MftDataRun(0, 100, 3) && runs[1] == new MftDataRun(3, -1, 2) && runs[2] == new MftDataRun(5, 90, 4), "Signed LCN delta/sparse VCN handling incorrect.");
        Assert(MftParser.DecodeRunList([0x11, 1, 12, 0], 9)[0].Vcn == 9, "Continuation starting VCN ignored.");
        foreach (var invalid in new byte[][] { [0x11, 0, 1, 0], [0x11, 1], [0x11, 1, 255, 0], [0x19, 1, 1, 0], [0x11, 1, 1] })
        { try { MftParser.DecodeRunList(invalid); throw new Exception("Malformed runlist accepted."); } catch (InvalidDataException) { } }
        try { MftParser.DecodeRunList([0x11, 2, 1, 0], long.MaxValue); throw new Exception("VCN overflow accepted."); } catch (OverflowException) { }
        return Done();
    }
    private static Task TestMft4Kn()
    {
        Assert(MftParser.TryParseRecord(MakeRecord(), 4096, out _, out var smallError), "1KiB FILE record on 4Kn volume rejected: " + smallError);
        var bytes = MakeRecord(4096);
        Assert(MftParser.TryParseRecord(bytes, 4096, out _, out var error), "Valid 4Kn record rejected: " + error);
        bytes[510] ^= 1;
        Assert(!MftParser.TryParseRecord(bytes, 4096, out _, out _), "Torn 512-byte fixup inside 4Kn record accepted.");
        return Done();
    }
    private static Task TestMftMalformed()
    {
        var valid = MakeRecord();
        var mutations = new List<byte[]>();
        var torn = (byte[])valid.Clone(); torn[510] ^= 1; mutations.Add(torn);
        var bounds = (byte[])valid.Clone(); Put32(bounds, 24, 2000); mutations.Add(bounds);
        var attrLength = (byte[])valid.Clone(); Put32(attrLength, 60, uint.MaxValue); mutations.Add(attrLength);
        var badUsa = (byte[])valid.Clone(); Put16(badUsa, 6, 40); mutations.Add(badUsa);
        var malformedName = (byte[])valid.Clone(); malformedName[56 + 24 + 64] = 255; mutations.Add(malformedName);
        foreach (var bytes in mutations) Assert(!MftParser.TryParseRecord(bytes, 512, out _, out _), "Malformed FILE record accepted.");
        Assert(!MftParser.TryParseRecord(valid.AsSpan(0, 100), 512, out _, out _), "Truncated FILE accepted.");
        return Done();
    }
    private static Task TestAttributeList()
    {
        var bytes = new byte[64];
        Put32(bytes, 0, 0x80); Put16(bytes, 4, 32); Put64(bytes, 16, 55UL | (12UL << 48));
        Put32(bytes, 32, 0x30); Put16(bytes, 36, 32); Put64(bytes, 48, 77UL | (8UL << 48));
        var references = MftParser.ParseAttributeList(bytes);
        Assert(references.SequenceEqual(new[] { 55UL | (12UL << 48), 77UL | (8UL << 48) }), "Attribute references lost sequence information.");
        Put16(bytes, 4, 0);
        try { MftParser.ParseAttributeList(bytes); throw new Exception("Zero-length attribute-list entry accepted."); } catch (InvalidDataException) { }
        return Done();
    }
    private static Task TestParserCorpus()
    {
        var random = new Random(710281);
        var valid = MakeRecord();
        for (int trial = 0; trial < 2500; trial++)
        {
            var bytes = trial % 2 == 0 ? (byte[])valid.Clone() : new byte[1024];
            if (trial % 2 != 0) random.NextBytes(bytes);
            for (int j = 0; j < 1 + trial % 8; j++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            _ = MftParser.TryParseRecord(bytes, 512, out _, out _);
        }
        return Done();
    }
    private static async Task TestWorkerTargets()
    {
        Assert(!WorkerClient.IsSupportedTarget(FixtureRoot), "Folder accepted as an elevated raw-volume target.");
        Assert(!WorkerClient.IsSupportedTarget(@"\\.\PhysicalDrive0"), "Physical drive accepted as a selected volume.");
        Assert(!WorkerClient.IsSupportedTarget(@"\\server\share\"), "Network root accepted for raw NTFS access.");
        try { await WorkerClient.ScanAsync(new(FixtureRoot, ScanEngineKind.Mft), null, CancellationToken.None); throw new Exception("Worker allowed a folder target."); }
        catch (ArgumentException) { }
    }
    private static async Task TestAlignedReads()
    {
        var path = Path.Combine(FixtureRoot, "aligned-pattern.bin");
        var pattern = new byte[5 * 1024 * 1024];
        for (int i = 0; i < pattern.Length; i++) pattern[i] = (byte)(i % 251);
        using (var write = System.IO.File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { RandomAccess.Write(write, pattern, 0); RandomAccess.FlushToDisk(write); }
        using var handle = CreateFileW(path, 0x80000000, 7, IntPtr.Zero, 3, 0x20000000, IntPtr.Zero);
        Assert(!handle.IsInvalid, "Cannot open noncached test fixture: Win32 " + Marshal.GetLastWin32Error());
        using var reader = new MftScanEngine.AlignedVolumeReader(handle, 4096, pattern.Length);
        foreach (var sample in new[] { (Offset: 3, Length: 123), (Offset: 4093, Length: 8199), (Offset: 101, Length: 4 * 1024 * 1024 + 79), (Offset: pattern.Length - 17, Length: 17) })
        {
            var observed = new byte[sample.Length];
            reader.ReadExactly(observed, sample.Offset);
            Assert(observed.AsSpan().SequenceEqual(pattern.AsSpan(sample.Offset, sample.Length)), "Aligned I/O returned incorrect bytes.");
        }
        try { reader.ReadExactly(new byte[1], -1); throw new Exception("Negative read accepted."); } catch (InvalidDataException) { }
        try { reader.ReadExactly(new byte[2], pattern.Length - 1); throw new Exception("Read past bound accepted."); } catch (InvalidDataException) { }
        await Task.CompletedTask;
    }
    private static async Task TestProtocolRoundtrip()
    {
        var original = FileNode("世界.bin", 0, 123456, -1, 45); original.Flags = NodeFlags.ReparseSkipped; original.Identity = new(45, 987);
        using var stream = new MemoryStream();
        using (var writer = new WorkerProtocol.Writer(stream))
            await writer.SendAsync(WorkerProtocol.Kind.Nodes, w => { w.Write(1); WorkerProtocol.WriteNode(w, original); });
        stream.Position = 0;
        var frame = await WorkerProtocol.ReceiveAsync(stream);
        using var reader = frame.Reader();
        Assert(frame.Kind == WorkerProtocol.Kind.Nodes && reader.ReadInt32() == 1, "Protocol frame kind/count incorrect.");
        var node = WorkerProtocol.ReadNode(reader);
        Assert(node.Name == original.Name && node.Identity == original.Identity && node.LogicalBytes == original.LogicalBytes && node.AllocatedBytes == -1 && node.Flags == original.Flags, "Protocol lost metadata.");
        var result = Model(Dir("root", -1), original);
        using var completion = new MemoryStream();
        using (var binary = new BinaryWriter(completion, Encoding.UTF8, true)) WorkerProtocol.WriteCompletion(binary, result);
        completion.Position = 0;
        using var completionReader = new BinaryReader(completion, Encoding.UTF8, true);
        var read = WorkerProtocol.ReadCompletion(completionReader, result.Nodes.ToList(), result.Target);
        Assert(read.Count == 2 && read.Engine == result.Engine && read.CompletedUtc == result.CompletedUtc, "Completion did not roundtrip.");
    }
    private static async Task TestProtocolMalformed()
    {
        foreach (var length in new[] { -1, 0, WorkerProtocol.MaximumFrame + 1 })
        {
            using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) writer.Write(length); stream.Position = 0;
            try { await WorkerProtocol.ReceiveAsync(stream); throw new Exception("Malformed frame length accepted."); } catch (InvalidDataException) { }
        }
        using (var stream = new MemoryStream(new byte[] { 4, 0, 0, 0, 1 }))
        { try { await WorkerProtocol.ReceiveAsync(stream); throw new Exception("Truncated frame accepted."); } catch (EndOfStreamException) { } }
        using (var stream = new MemoryStream(new byte[] { 1, 0, 0, 0, 255 }))
        { try { await WorkerProtocol.ReceiveAsync(stream); throw new Exception("Unknown message accepted."); } catch (InvalidDataException) { } }
        using (var stream = new MemoryStream(new byte[] { 100, 0, 0, 0, 1 }))
        using (var reader = new BinaryReader(stream))
        { try { WorkerProtocol.ReadText(reader); throw new Exception("Truncated string accepted."); } catch (InvalidDataException) { } }
        using (var stream = new MemoryStream())
        using (var writer = new WorkerProtocol.Writer(stream))
        { try { await writer.SendAsync(WorkerProtocol.Kind.Error, data => data.Write(new byte[WorkerProtocol.MaximumFrame])); throw new Exception("Oversized outgoing frame accepted."); } catch (InvalidDataException) { } }
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        { try { WorkerProtocol.WriteText(writer, new string('x', 131073)); throw new Exception("Oversized protocol string accepted."); } catch (InvalidDataException) { } }
        var result = Model(Dir("root", -1));
        using var memory = new MemoryStream(); using (var writer = new BinaryWriter(memory, Encoding.UTF8, true)) WorkerProtocol.WriteCompletion(writer, result); memory.Position = 0;
        using var read = new BinaryReader(memory);
        try { WorkerProtocol.ReadCompletion(read, result.Nodes.ToList(), @"D:\different"); throw new Exception("Mismatched target accepted."); } catch (InvalidDataException) { }
    }

    private static async Task TestRealDirectory()
    {
        var root = NewFixture("regular");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "résumé,世界.txt"), "Unicode and comma in filename");
        var deep = root;
        for (int i = 0; i < 14; i++) { deep = Path.Combine(deep, "deep-directory-" + i.ToString("D2")); Directory.CreateDirectory(deep); }
        await System.IO.File.WriteAllBytesAsync(Path.Combine(deep, "long-path.bin"), new byte[8193]);
        for (int i = 0; i < 300; i++) await System.IO.File.WriteAllBytesAsync(Path.Combine(root, "file-" + i.ToString("D4") + ".bin"), new byte[500 + i]);
        var (optimized, baseline) = await Compare(root);
        Assert(optimized.Count == 318 && optimized.FileCounts[0] == 302, $"Unexpected real fixture entries: {optimized.Count} files {optimized.FileCounts[0]}.");
        Assert(!optimized.Partial && !baseline.Partial, "Ordinary controlled fixture unexpectedly partial.");
    }
    private static async Task TestRealHardLinks()
    {
        var root = NewFixture("hardlinks");
        var original = Path.Combine(root, "z.bin");
        await System.IO.File.WriteAllBytesAsync(original, new byte[10000]);
        if (!CreateHardLinkW(Path.Combine(root, "a.bin"), original, IntPtr.Zero)) throw new Skipped("Hard links unavailable: Win32 " + Marshal.GetLastWin32Error());
        var (optimized, baseline) = await Compare(root);
        var owner = optimized.Nodes.Single(n => !n.IsDirectory && n.Name == "a.bin");
        Assert(optimized.LogicalTotals[0] == 20000 && owner.AllocatedBytes >= 0 &&
            (owner.Flags & NodeFlags.SharedAllocation) == 0 && optimized.AllocatedTotals[0] == owner.AllocatedBytes,
            "Real hard-link storage was not attributed once to the canonical owning path.");
        Assert(optimized.Nodes.Count(n => (n.Flags & NodeFlags.SharedAllocation) != 0) == 1, "Shared indicator absent.");
    }
    private static async Task TestSparse()
    {
        var root = NewFixture("sparse");
        var path = Path.Combine(root, "sparse.bin");
        using (var handle = System.IO.File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            if (!DeviceIoControl(handle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Skipped("Sparse files unsupported: Win32 " + Marshal.GetLastWin32Error());
            RandomAccess.SetLength(handle, 16 * 1024 * 1024);
            RandomAccess.Write(handle, new byte[4096], 0);
            RandomAccess.FlushToDisk(handle);
        }
        var (optimized, baseline) = await Compare(root);
        Assert(optimized.LogicalTotals[0] == 16 * 1024 * 1024 && optimized.AllocatedTotals[0] < optimized.LogicalTotals[0] / 10, "Sparse holes counted as physical storage.");
    }
    private static async Task TestCompressed()
    {
        var root = NewFixture("compressed");
        var path = Path.Combine(root, "compressed.bin");
        using (var handle = System.IO.File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            ushort compression = 2;
            if (!SetCompression(handle, 0x9C040, ref compression, 2, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Skipped("NTFS compression unsupported: Win32 " + Marshal.GetLastWin32Error());
            RandomAccess.Write(handle, new byte[1024 * 1024], 0);
            RandomAccess.FlushToDisk(handle);
        }
        var (optimized, baseline) = await Compare(root);
        Assert(optimized.LogicalTotals[0] == 1024 * 1024 && optimized.AllocatedTotals[0] < optimized.LogicalTotals[0] / 2, "Compressed storage was not measured.");
    }
    private static async Task TestAlternateStream()
    {
        var root = NewFixture("streams");
        var path = Path.Combine(root, "base.txt");
        await System.IO.File.WriteAllTextAsync(path, "main");
        try { await System.IO.File.WriteAllBytesAsync(path + ":additional", new byte[1024 * 1024]); }
        catch (IOException ex) { throw new Skipped(ex.Message); }
        var (optimized, baseline) = await Compare(root);
        Assert(optimized.LogicalTotals[0] == 4 && optimized.AllocatedTotals[0] < 1024 * 1024, "Named stream included in default accounting.");
    }
    private static async Task TestReparseLoop()
    {
        var root = NewFixture("reparse");
        await System.IO.File.WriteAllTextAsync(Path.Combine(root, "real.txt"), "real");
        var loop = Path.Combine(root, "loop");
        Directory.CreateDirectory(loop);
        MakeJunction(loop, root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory), null, timeout.Token);
        Assert(result.Count == 3 && !result.Cancelled, "Reparse loop traversed.");
        Assert(result.Nodes.Any(n => (n.Flags & NodeFlags.ReparseSkipped) != 0) && result.UnknownCounts[0] > 0, "Skipped reparse subtree coverage absent.");
    }
    private static async Task TestDenied()
    {
        var root = NewFixture("denied");
        var secret = Path.Combine(root, "secret"); Directory.CreateDirectory(secret);
        await System.IO.File.WriteAllTextAsync(Path.Combine(secret, "unreadable.txt"), "secret");
        var info = new DirectoryInfo(secret);
        var before = info.GetAccessControl(AccessControlSections.Access);
        var descriptor = before.GetSecurityDescriptorBinaryForm();
        var changed = new DirectorySecurity(); changed.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new Skipped("No Windows user SID.");
        changed.AddAccessRule(new(sid, FileSystemRights.ListDirectory, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            try { info.SetAccessControl(changed); } catch (UnauthorizedAccessException ex) { throw new Skipped(ex.Message); }
            var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory));
            Assert(result.Partial && result.Issues.Count != 0 && result.UnknownCounts[0] > 0, "Denied subtree presented as complete.");
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            info.SetAccessControl(restore);
        }
    }
    private static async Task TestLocked()
    {
        var root = NewFixture("locked");
        var path = Path.Combine(root, "locked.bin");
        await System.IO.File.WriteAllBytesAsync(path, new byte[4096]);
        using var locked = System.IO.File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Baseline));
        Assert(result.FileCounts[0] == 1 && result.LogicalTotals[0] == 4096 && result.UnknownCounts[0] == 0, "Metadata-only scan tried to read locked content.");
    }
    private static async Task TestCancellation()
    {
        var root = NewFixture("cancellation");
        for (int i = 0; i < 80; i++)
        {
            var dir = Path.Combine(root, i.ToString("D3")); Directory.CreateDirectory(dir);
            for (int j = 0; j < 80; j++) System.IO.File.WriteAllText(Path.Combine(dir, j.ToString("D3") + ".txt"), "x");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory), null, cancellation.Token);
        Assert(result.Cancelled && result.Partial && result.Count > 0 && result.UnknownCounts[0] > 0, "Cancellation did not retain unvisited root as incomplete.");
        // Many directory handles keep work queued; cancellation comes from its own OS thread,
        // because a ThreadPool timer can be delayed by synchronous enumeration workers.
        for (int i = 0; i < 2048; i++) Directory.CreateDirectory(Path.Combine(root, "queued-" + i.ToString("D4")));
        using var midScan = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var stopper = new Thread(() => { started.Wait(); Thread.Sleep(2); midScan.Cancel(); }) { IsBackground = true };
        stopper.Start();
        var scanning = ScanService.ScanAsync(new(root, ScanEngineKind.Directory, 2), null, midScan.Token);
        started.Set();
        var activeResult = await scanning;
        stopper.Join();
        Assert(activeResult.Cancelled && activeResult.Partial && activeResult.UnknownCounts[0] > 0,
            $"Stopped directory queue was presented as complete (cancelled={activeResult.Cancelled}, unknown={activeResult.UnknownCounts[0]}, entries={activeResult.Count}, elapsed={activeResult.Elapsed.TotalMilliseconds:N1}ms).");
    }
    private static async Task TestChanging()
    {
        var root = NewFixture("changing");
        for (int i = 0; i < 300; i++) System.IO.File.WriteAllText(Path.Combine(root, i.ToString("D4") + ".txt"), "changing");
        using var writerStop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 1000 && !writerStop.IsCancellationRequested; i++)
            {
                var path = Path.Combine(root, "live-" + i.ToString("D4") + ".txt");
                try { System.IO.File.WriteAllText(path, "live"); System.IO.File.Delete(path); } catch (IOException) { }
            }
        });
        try { var result = await ScanService.ScanAsync(new(root, ScanEngineKind.Directory)); Assert(result.Count >= 301 && result.LogicalTotals[0] >= 2400, "Stable entries disappeared under concurrent changes."); }
        finally { writerStop.Cancel(); await writer; }
    }

    private static async Task<(ScanResult Optimized, ScanResult Baseline)> Compare(string path)
    {
        var optimized = await ScanService.ScanAsync(new(path, ScanEngineKind.Directory));
        var baseline = await ScanService.ScanAsync(new(path, ScanEngineKind.Baseline));
        Assert(optimized.Count == baseline.Count, $"Entry counts differ: {optimized.Count}/{baseline.Count}");
        Assert(optimized.LogicalTotals[0] == baseline.LogicalTotals[0], $"Logical totals differ: {optimized.LogicalTotals[0]}/{baseline.LogicalTotals[0]}");
        Assert(optimized.AllocatedTotals[0] == baseline.AllocatedTotals[0], $"Allocation totals differ: {optimized.AllocatedTotals[0]}/{baseline.AllocatedTotals[0]}");
        Assert(optimized.UnknownCounts[0] == baseline.UnknownCounts[0], "Unknown counts differ.");
        var a = Enumerable.Range(1, optimized.Count - 1).ToDictionary(optimized.GetPath, StringComparer.OrdinalIgnoreCase);
        foreach (var index in Enumerable.Range(1, baseline.Count - 1))
        {
            var name = baseline.GetPath(index); Assert(a.TryGetValue(name, out var other), "Entry missing: " + name);
            Assert(optimized.Nodes[other].LogicalBytes == baseline.Nodes[index].LogicalBytes && optimized.Nodes[other].AllocatedBytes == baseline.Nodes[index].AllocatedBytes, "Per-entry size mismatch: " + name);
        }
        return (optimized, baseline);
    }
    private static string NewFixture(string name) { var path = Path.Combine(FixtureRoot, name); Directory.CreateDirectory(path); return path; }
    private static byte[] MakeRecord(int length = 1024)
    {
        var b = new byte[length];
        int usaCount = length / 512 + 1, firstAttribute = (48 + usaCount * 2 + 7) & ~7;
        "FILE"u8.CopyTo(b); Put16(b, 4, 48); Put16(b, 6, (ushort)usaCount); Put16(b, 16, 7); Put16(b, 20, (ushort)firstAttribute); Put16(b, 22, 1); Put32(b, 28, (uint)length); Put32(b, 44, 123);
        Put16(b, 48, 0xA55A);
        for (int sector = 1; sector < usaCount; sector++) { Put16(b, 48 + sector * 2, 0); Put16(b, sector * 512 - 2, 0xA55A); }
        var name = Encoding.Unicode.GetBytes("測試.txt");
        var value = new byte[66 + name.Length]; Put64(value, 0, 5UL | (3UL << 48)); value[64] = (byte)(name.Length / 2); value[65] = 1; name.CopyTo(value, 66);
        int cursor = firstAttribute; cursor += ResidentAttribute(b, cursor, 0x30, value); cursor += ResidentAttribute(b, cursor, 0x80, [1, 2, 3]); Put32(b, cursor, uint.MaxValue); Put32(b, 24, (uint)(cursor + 4));
        return b;
    }
    private static int ResidentAttribute(byte[] record, int offset, uint type, byte[] value)
    {
        int length = (24 + value.Length + 7) & ~7;
        Put32(record, offset, type); Put32(record, offset + 4, (uint)length); Put32(record, offset + 16, (uint)value.Length); Put16(record, offset + 20, 24); value.CopyTo(record, offset + 24); return length;
    }
    private static void Put16(byte[] b, int p, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p, 2), value);
    private static void Put32(byte[] b, int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p, 4), value);
    private static void Put64(byte[] b, int p, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(p, 8), value);
    private static void MakeJunction(string junction, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        Put32(data, 0, 0xA0000003); Put16(data, 4, checked((ushort)(data.Length - 8))); Put16(data, 8, 0); Put16(data, 10, (ushort)substitute.Length); Put16(data, 12, (ushort)(substitute.Length + 2)); Put16(data, 14, (ushort)print.Length);
        substitute.CopyTo(data, 16); print.CopyTo(data, 16 + substitute.Length + 2);
        using var handle = CreateFileW(junction, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Skipped("Cannot open junction fixture: Win32 " + Marshal.GetLastWin32Error());
        if (!SetReparse(handle, 0x900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Skipped("Cannot set junction fixture: Win32 " + Marshal.GetLastWin32Error());
    }
    private sealed record TestOutcome(string Name, string Status, double Milliseconds, string? Detail);
    private sealed class Skipped(string message) : Exception(message);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCompression(SafeFileHandle handle, uint code, ref ushort input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetReparse(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
