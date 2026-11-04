using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskPeek.Core;

/// <summary>Read-only, sequential scan of the extents belonging to a local NTFS $MFT.</summary>
public sealed class MftScanEngine : IScanEngine
{
    public string Name => "NTFS MFT";
    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Scan(request, progress, cancellationToken), CancellationToken.None);

    private ScanResult Scan(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        string target = Path.GetFullPath(request.Target);
        string root = Path.GetPathRoot(target) ?? "";
        if (root.Length != 3 || root[1] != ':' || !string.Equals(target.TrimEnd('\\', '/'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Direct NTFS scans require a local drive root. Folders use directory scanning.");
        var drive = new DriveInfo(root);
        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new NotSupportedException("Direct metadata scanning requires a local NTFS volume.");
        using var volume = Native.CreateFileW(@"\\.\" + root[..2], 0x80000000, 1 | 2 | 4, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Read-only volume access requires administrator privileges.");
        byte[] geometry = new byte[128];
        if (!Native.DeviceIoControl(volume, 0x00090064, null, 0, geometry, geometry.Length, out int returned, IntPtr.Zero) || returned < 96)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query NTFS volume geometry.");
        int sector = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(40)));
        int cluster = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(44)));
        int recordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(48)));
        long mftLength = BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(56));
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(64));
        long totalClusters = BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(16));
        if (sector is < 512 or > 65536 || (sector & (sector - 1)) != 0 || cluster < sector || cluster % sector != 0 || recordSize is < 512 or > 65536 || recordSize % 512 != 0 || mftLength <= 0 || mftLcn < 0 || totalClusters <= 0)
            throw new InvalidDataException("Unsupported NTFS geometry.");
        if (returned < 104 || BinaryPrimitives.ReadUInt16LittleEndian(geometry.AsSpan(100)) != 3 || BinaryPrimitives.ReadUInt16LittleEndian(geometry.AsSpan(102)) > 1)
            throw new NotSupportedException("Only documented NTFS 3.0/3.1 record layouts are supported.");
        long volumeLength = checked(totalClusters * cluster);
        using var volumeReader = new AlignedVolumeReader(volume, sector, volumeLength);
        var stopwatch = Stopwatch.StartNew();
        var issues = new List<ScanIssue>();
        long skipped = 0;
        bool partial = false;
        void Issue(string message)
        {
            skipped++; partial = true;
            if (issues.Count < 1000) issues.Add(new(target, message));
        }
        byte[] recordZero = new byte[recordSize];
        volumeReader.ReadExactly(recordZero, checked(mftLcn * cluster));
        if (!MftParser.TryParseRecord(recordZero, sector, out var bootstrap, out var bootError) || !bootstrap.InUse || bootstrap.Sequence == 0 || bootstrap.BaseReference != 0)
            throw new InvalidDataException("Cannot bootstrap $MFT: " + bootError);
        // NTFS 3.0's shorter header can place USA words where newer headers store the record number.
        // Its authoritative number is zero because it was read from the advertised $MFT starting LCN.
        bootstrap.RecordNumber = 0;
        // $MFT may itself have an ATTRIBUTE_LIST and a fragmented stream. Resolve every extension before reading it.
        var bootRecords = new List<MftRecord> { bootstrap };
        var visited = new HashSet<ulong> { bootstrap.Reference };
        var pending = new Queue<ulong>();
        foreach (ulong reference in ReadReferences(bootstrap, volumeReader, cluster)) pending.Enqueue(reference);
        while (pending.TryDequeue(out ulong reference))
        {
            if (!visited.Add(reference)) continue;
            if (visited.Count > 65536) throw new InvalidDataException("Excessive $MFT extension count.");
            var extension = ReadRecordByReference(volume, reference, recordSize, sector);
            if (!extension.InUse || extension.BaseReference != bootstrap.Reference)
                throw new InvalidDataException("Invalid $MFT extension owner/sequence.");
            bootRecords.Add(extension);
            foreach (var next in ReadReferences(extension, volumeReader, cluster)) pending.Enqueue(next);
        }
        var mftRuns = bootRecords.SelectMany(r => r.DataStreams).Where(s => s.Name.Length == 0 && !s.Resident)
            .SelectMany(s => s.RunList).OrderBy(r => r.Vcn).ToArray();
        long coveredVcn = 0;
        foreach (var run in mftRuns)
        {
            if (run.Vcn != coveredVcn || run.Lcn < 0 || run.Lcn + run.ClusterCount > totalClusters)
                throw new InvalidDataException("Invalid, incomplete, or sparse $MFT extents.");
            coveredVcn = checked(coveredVcn + run.ClusterCount);
        }
        if (checked(coveredVcn * cluster) < mftLength) throw new InvalidDataException("Incomplete $MFT stream mapping.");
        mftLength -= mftLength % recordSize;
        var entries = new List<MftEntry>(Math.Min(1_000_000, checked((int)Math.Min(int.MaxValue, mftLength / recordSize))));
        var complex = new Dictionary<ulong, MftRecord>();
        var extensions = new Dictionary<ulong, MftRecord>();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        long parsed = 0;
        var lastProgress = TimeSpan.Zero;
        try
        {
            for (long offset = 0; offset < mftLength && !ct.IsCancellationRequested;)
            {
                int length = (int)Math.Min(chunk.Length - chunk.Length % recordSize, mftLength - offset);
                ReadMapped(volumeReader, chunk.AsSpan(0, length), offset, mftRuns, cluster);
                for (int cursor = 0; cursor < length; cursor += recordSize)
                {
                    if ((cursor & 0xffff) == 0 && ct.IsCancellationRequested) break;
                    var data = chunk.AsSpan(cursor, recordSize);
                    ulong recordNumber = (ulong)((offset + cursor) / recordSize);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(data) == 0) continue;
                    if (!data[..4].SequenceEqual("FILE"u8)) { Issue($"Invalid signature at MFT record {recordNumber}."); continue; }
                    if ((BinaryPrimitives.ReadUInt16LittleEndian(data[22..]) & 1) == 0) continue;
                    if (!MftParser.TryParseRecord(data, sector, out var record, out string? error))
                    {
                        Issue($"MFT record {recordNumber}: {error}"); continue;
                    }
                    // The stream location is authoritative for the 48-bit record number.
                    record.RecordNumber = recordNumber;
                    if (record.Sequence == 0) { Issue($"MFT record {recordNumber} has an invalid zero sequence."); continue; }
                    if (record.BaseReference != 0) extensions[record.Reference] = record;
                    else if (record.AttributeReferences.Count != 0 || record.ExternalAttributeLists.Count != 0) complex[record.Reference] = record;
                    else if (!AddEntries(record, [record], entries, cluster, totalClusters, false))
                        Issue($"MFT record {recordNumber} has no usable name or incomplete stream metadata.");
                    parsed++;
                }
                offset += length;
                if (stopwatch.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(100))
                {
                    progress?.Report(new(parsed, 0, stopwatch.Elapsed, $"Reading MFT • {offset * 100d / mftLength:F0}%", Name));
                    lastProgress = stopwatch.Elapsed;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
        progress?.Report(new(parsed, 0, stopwatch.Elapsed, "Resolving metadata", Name));
        foreach (var baseRecord in complex.Values)
        {
            var records = new List<MftRecord> { baseRecord };
            var seen = new HashSet<ulong> { baseRecord.Reference };
            var references = new Queue<ulong>();
            bool incomplete = false;
            try
            {
                if (ct.IsCancellationRequested) incomplete = true;
                else foreach (var reference in ReadReferences(baseRecord, volumeReader, cluster)) references.Enqueue(reference);
                while (references.TryDequeue(out ulong reference))
                {
                    if (!seen.Add(reference)) continue;
                    if (seen.Count > 65536) throw new InvalidDataException("Excessive attribute continuation count.");
                    if (!extensions.TryGetValue(reference, out var extension))
                    {
                        // A cancelled scan should not continue issuing isolated I/O to complete the entire volume.
                        if (ct.IsCancellationRequested) { incomplete = true; continue; }
                        extension = ReadRecordByReference(volume, reference, recordSize, sector);
                    }
                    if (!extension.InUse || extension.BaseReference != baseRecord.Reference)
                        throw new InvalidDataException("Extension reference no longer matches its owner.");
                    records.Add(extension);
                    if (ct.IsCancellationRequested) incomplete = true;
                    else foreach (var next in ReadReferences(extension, volumeReader, cluster)) references.Enqueue(next);
                }
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or OverflowException)
            { incomplete = true; Issue($"Record {baseRecord.RecordNumber} attribute continuation: {ex.Message}"); }
            if (!AddEntries(baseRecord, records, entries, cluster, totalClusters, incomplete))
            {
                partial = true;
                if (!incomplete) Issue($"MFT record {baseRecord.RecordNumber} has no usable name or incomplete stream metadata.");
            }
        }
        var nodes = new List<ScanNode>(entries.Count + 2)
        {
            new() { Name = target, ParentIndex = -1, Attributes = FileAttributes.Directory, AllocatedBytes = 0 }
        };
        var dirs = new Dictionary<ulong, int>();
        // Root record 5 must be identified by its actual sequence number, not an assumed value.
        foreach (var entry in entries)
            if ((entry.Reference & 0x0000ffffffffffffUL) == 5)
            {
                dirs[entry.Reference] = 0;
                var rootNode = nodes[0]; rootNode.Identity = new(entry.Reference); nodes[0] = rootNode;
            }
        var referencesForNode = new List<ulong>(entries.Count + 2) { 0 };
        // Excluding only immediate children of reserved records would reintroduce their descendants as orphans.
        var directoryParents = new Dictionary<ulong, ulong>();
        var excludedDirectories = new HashSet<ulong>();
        var acceptedDirectories = new HashSet<ulong>(dirs.Keys);
        foreach (var entry in entries)
            if (entry.IsDirectory)
            {
                directoryParents.TryAdd(entry.Reference, entry.Parent);
                ulong number = entry.Reference & 0x0000ffffffffffffUL;
                if (number < 16 && number != 5) excludedDirectories.Add(entry.Reference);
            }
        var parentChain = new List<ulong>();
        var chainSeen = new HashSet<ulong>();
        foreach (ulong reference in directoryParents.Keys)
        {
            parentChain.Clear(); chainSeen.Clear();
            ulong current = reference;
            while (!excludedDirectories.Contains(current) && !acceptedDirectories.Contains(current) &&
                directoryParents.TryGetValue(current, out ulong parent) && chainSeen.Add(current))
            { parentChain.Add(current); current = parent; }
            var destination = excludedDirectories.Contains(current) ? excludedDirectories : acceptedDirectories;
            foreach (ulong item in parentChain) destination.Add(item);
        }
        foreach (var entry in entries)
        {
            ulong number = entry.Reference & 0x0000ffffffffffffUL;
            if (number < 16) continue; // NTFS metadata is excluded from the file-data total.
            if ((entry.Parent & 0x0000ffffffffffffUL) < 16 && (entry.Parent & 0x0000ffffffffffffUL) != 5) continue;
            if (excludedDirectories.Contains(entry.Parent) || (entry.IsDirectory && excludedDirectories.Contains(entry.Reference))) continue;
            if (entry.IsDirectory && dirs.ContainsKey(entry.Reference)) continue;
            int index = nodes.Count;
            nodes.Add(new()
            {
                Name = entry.Name, ParentIndex = -1, Identity = new(entry.Reference),
                LogicalBytes = entry.Logical, AllocatedBytes = entry.Allocated,
                ModifiedUtcTicks = entry.Modified, Attributes = entry.Attributes, Flags = entry.Flags
            });
            referencesForNode.Add(entry.Parent);
            if (entry.IsDirectory) dirs[entry.Reference] = index;
        }
        int unresolved = -1;
        int Unresolved()
        {
            if (unresolved >= 0) return unresolved;
            unresolved = nodes.Count;
            nodes.Add(new() { Name = "[Unresolved entries]", ParentIndex = 0, Attributes = FileAttributes.Directory, Flags = NodeFlags.Inaccessible });
            referencesForNode.Add(0);
            return unresolved;
        }
        int initialCount = nodes.Count;
        for (int i = 1; i < initialCount; i++)
        {
            var node = nodes[i];
            if (dirs.TryGetValue(referencesForNode[i], out int parent) && parent != i) node.ParentIndex = parent;
            else { node.ParentIndex = Unresolved(); partial = true; }
            nodes[i] = node;
        }
        // Mark cycles before aggregation, and suppress descendants of reparse directories.
        var states = new byte[nodes.Count]; // 0 unseen, 1 visiting, 2 accepted, 3 excluded
        states[0] = 2;
        for (int i = 1; i < initialCount; i++)
        {
            if (states[i] != 0) continue;
            var chain = new List<int>();
            int cursor = i;
            while (cursor > 0 && states[cursor] == 0)
            {
                states[cursor] = 1; chain.Add(cursor);
                cursor = nodes[cursor].ParentIndex;
            }
            bool exclude = cursor >= 0 && (states[cursor] == 3 || (nodes[cursor].Flags & NodeFlags.ReparseSkipped) != 0);
            if (cursor > 0 && states[cursor] == 1)
            {
                var broken = nodes[cursor]; broken.ParentIndex = Unresolved(); nodes[cursor] = broken;
                // Unresolved() can grow states by one on the first malformed cycle.
                if (states.Length < nodes.Count) Array.Resize(ref states, nodes.Count);
                partial = true; Issue("Cyclic parent reference was moved to unresolved entries.");
            }
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int index = chain[k];
                states[index] = exclude ? (byte)3 : (byte)2;
                if ((nodes[index].Flags & NodeFlags.ReparseSkipped) != 0) exclude = true;
            }
        }
        var remap = new int[nodes.Count]; Array.Fill(remap, -1);
        int kept = 0;
        for (int i = 0; i < nodes.Count; i++) if (i >= states.Length || states[i] != 3) remap[i] = kept++;
        var final = new ScanNode[kept];
        for (int i = 0; i < nodes.Count; i++)
        {
            if (remap[i] < 0) continue;
            var node = nodes[i];
            if (node.ParentIndex >= 0) node.ParentIndex = remap[node.ParentIndex];
            final[remap[i]] = node;
        }
        stopwatch.Stop();
        return new()
        {
            Target = target, Nodes = final, Engine = Name, Elapsed = stopwatch.Elapsed,
            Cancelled = ct.IsCancellationRequested, Partial = partial || ct.IsCancellationRequested,
            Issues = issues, SkippedEntries = skipped, VolumeCapacity = drive.TotalSize, VolumeFree = drive.AvailableFreeSpace
        };
    }

    private readonly record struct MftEntry(ulong Reference, ulong Parent, string Name, long Logical, long Allocated, long Modified, FileAttributes Attributes, NodeFlags Flags)
    { public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0; }

    private static bool AddEntries(MftRecord baseRecord, IReadOnlyList<MftRecord> records, List<MftEntry> entries, int cluster, long totalClusters, bool incomplete)
    {
        int originalCount = entries.Count;
        var names = records.SelectMany(r => r.Names).Where(n => n.Namespace is 0 or 1 or 3)
            .DistinctBy(n => (n.ParentReference, n.Name)).ToArray();
        if (names.Length == 0 && baseRecord.RecordNumber != 5) return false;
        long logical = 0, allocated = 0;
        NodeFlags flags = NodeFlags.None;
        var streams = records.SelectMany(r => r.DataStreams).ToArray();
        if (streams.Any(s => s.Name.Length != 0)) flags |= NodeFlags.AdditionalStreams;
        var main = streams.Where(s => s.Name.Length == 0).OrderBy(s => s.StartingVcn).ToArray();
        if (!baseRecord.IsDirectory && main.Length == 0) incomplete = true;
        if (main.Length > 1 && main.Any(s => s.Resident)) incomplete = true;
        try
        {
            long expectedVcn = 0;
            foreach (var stream in main)
            {
                if (stream.StartingVcn == 0) logical = stream.LogicalSize;
                if (stream.Resident) continue;
                if (stream.StartingVcn != expectedVcn) incomplete = true;
                foreach (var run in stream.RunList)
                    if (run.Lcn >= 0)
                    {
                        if (checked(run.Lcn + run.ClusterCount) > totalClusters) incomplete = true;
                        allocated = checked(allocated + checked(run.ClusterCount * cluster));
                    }
                expectedVcn = stream.EndingVcn + 1;
            }
            if (main.Any(s => !s.Resident) && logical > checked(expectedVcn * cluster)) incomplete = true;
        }
        catch (OverflowException) { incomplete = true; }
        var attributes = baseRecord.Attributes & ~FileAttributes.Directory;
        if (baseRecord.IsDirectory) attributes |= FileAttributes.Directory;
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000)) != 0)
        {
            allocated = -1;
            if (baseRecord.IsDirectory) flags |= NodeFlags.ReparseSkipped;
        }
        if (incomplete) { allocated = -1; flags |= NodeFlags.Inaccessible; }
        if (baseRecord.RecordNumber == 5)
            entries.Add(new(baseRecord.Reference, 0, "", 0, 0, baseRecord.ModifiedUtcTicks, attributes, flags));
        foreach (var name in names)
        {
            if (name.Name is "" or "." or ".." || name.Name.IndexOfAny(['\\', '/', '\0']) >= 0) continue;
            entries.Add(new(baseRecord.Reference, name.ParentReference, name.Name, logical, allocated, baseRecord.ModifiedUtcTicks, attributes, flags));
        }
        return !incomplete && entries.Count > originalCount;
    }

    private static IEnumerable<ulong> ReadReferences(MftRecord record, AlignedVolumeReader volume, int cluster)
    {
        var references = new HashSet<ulong>(record.AttributeReferences);
        foreach (var stream in record.ExternalAttributeLists)
        {
            if (stream.StartingVcn != 0 || stream.LogicalSize is < 0 or > 8 * 1024 * 1024)
                throw new NotSupportedException("Unsupported fragmented attribute-list stream.");
            var bytes = new byte[checked((int)stream.LogicalSize)];
            ReadMapped(volume, bytes, 0, stream.RunList, cluster);
            foreach (var reference in MftParser.ParseAttributeList(bytes)) references.Add(reference);
        }
        return references;
    }
    private static MftRecord ReadRecordByReference(SafeFileHandle volume, ulong reference, int recordSize, int sector)
    {
        // This control operates on record numbers; sequence validation comes from the returned FILE header.
        byte[] input = BitConverter.GetBytes(reference & 0x0000ffffffffffffUL);
        byte[] output = new byte[recordSize + 16];
        if (!Native.DeviceIoControl(volume, 0x00090068, input, input.Length, output, output.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read an NTFS extension record.");
        if (returned < 12 || (BinaryPrimitives.ReadUInt64LittleEndian(output) & 0x0000ffffffffffffUL) != (reference & 0x0000ffffffffffffUL) || BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(8)) != recordSize || returned < 12 + recordSize)
            throw new InvalidDataException("NTFS returned a different or truncated extension record.");
        if (!MftParser.TryParseRecord(output.AsSpan(12, recordSize), sector, out var record, out string? error))
            throw new InvalidDataException("Invalid NTFS extension: " + error);
        record.RecordNumber = reference & 0x0000ffffffffffffUL;
        if (record.Sequence == 0 || record.Reference != reference) throw new InvalidDataException("NTFS extension sequence changed.");
        return record;
    }
    private static void ReadMapped(AlignedVolumeReader handle, Span<byte> buffer, long logicalOffset, IReadOnlyList<MftDataRun> runs, int cluster)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            long position = checked(logicalOffset + done);
            long vcn = position / cluster;
            // Binary search matters for heavily fragmented MFTs.
            int lo = 0, hi = runs.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                var run = runs[mid];
                if (vcn < run.Vcn) hi = mid - 1;
                else if (vcn >= checked(run.Vcn + run.ClusterCount)) lo = mid + 1;
                else { found = mid; break; }
            }
            if (found < 0 || runs[found].Lcn < 0) throw new InvalidDataException("Missing/sparse mapped stream extent.");
            var mapping = runs[found];
            long within = position - checked(mapping.Vcn * cluster);
            int length = (int)Math.Min(buffer.Length - done, checked(mapping.ClusterCount * cluster) - within);
            long physical = checked(mapping.Lcn * cluster + within);
            handle.ReadExactly(buffer.Slice(done, length), physical);
            done += length;
        }
    }
    internal sealed unsafe class AlignedVolumeReader : IDisposable
    {
        private const int MaximumTransfer = 4 * 1024 * 1024;
        private readonly SafeFileHandle handle;
        private readonly int sector;
        private readonly long volumeLength;
        private byte* scratch;

        internal AlignedVolumeReader(SafeFileHandle handle, int sector, long volumeLength)
        {
            this.handle = handle; this.sector = sector; this.volumeLength = volumeLength;
            // One reusable aligned allocation per scan covers physical sectors up to 64 KiB.
            // Extra space accommodates rounding either edge of a small attribute-list read.
            scratch = (byte*)NativeMemory.AlignedAlloc(MaximumTransfer + 2 * 65536, 65536);
            if (scratch == null) throw new OutOfMemoryException();
        }
        internal void ReadExactly(Span<byte> destination, long offset)
        {
            if (offset < 0 || checked(offset + destination.Length) > volumeLength)
                throw new InvalidDataException("Read exceeds the NTFS volume.");
            int done = 0;
            while (done < destination.Length)
            {
                int requested = Math.Min(MaximumTransfer, destination.Length - done);
                long position = checked(offset + done), alignedOffset = position - position % sector;
                int prefix = checked((int)(position - alignedOffset));
                int transfer = checked((prefix + requested + sector - 1) / sector * sector);
                if (checked(alignedOffset + transfer) > volumeLength)
                    throw new InvalidDataException("Aligned read exceeds the NTFS volume.");
                var aligned = new Span<byte>(scratch, transfer);
                // A retry into a sliced buffer could lose physical-sector address alignment.
                // Volume reads should complete whole sectors; a short transfer falls back cleanly.
                if (RandomAccess.Read(handle, aligned, alignedOffset) != transfer)
                    throw new NotSupportedException("The volume returned a partial direct read.");
                aligned.Slice(prefix, requested).CopyTo(destination.Slice(done, requested));
                done += requested;
            }
        }
        public void Dispose()
        {
            if (scratch != null) { NativeMemory.AlignedFree(scratch); scratch = null; }
        }
    }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    }
}
