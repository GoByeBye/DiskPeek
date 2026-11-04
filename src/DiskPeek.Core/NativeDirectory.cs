using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskPeek.Core;

internal static class NativeDirectory
{
    internal readonly record struct Entry(string Name, FileIdentity Identity, long Logical, long Allocated,
        long ModifiedUtcTicks, FileAttributes Attributes, NodeFlags Flags, int MetadataError = 0);
    private const uint ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000,
        OpenReparsePoint = 0x00200000, OpenNoRecall = 0x00100000;
    private const int BufferSize = 128 * 1024;
    private const FileAttributes RemoteData = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    internal static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    internal static FileAttributes RootAttributes(string path)
    {
        var attributes = GetFileAttributesW(ExtendedPath(path));
        if (attributes == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (FileAttributes)attributes;
    }

    internal static void Read(string path, CancellationToken token, Action<List<Entry>> onBatch)
    {
        using var handle = CreateFileW(ExtendedPath(path), 0x81, ShareAll, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint | OpenNoRecall, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Re-check the opened object: a normal directory may have become a junction since its parent was read.
        if (!GetAttributeTagInfo(handle, 9, out var opened, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (((FileAttributes)opened.Attributes & (FileAttributes.ReparsePoint | RemoteData)) != 0)
            throw new IOException("Reparse or offline directory not traversed.");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            // Unsupported information classes must restart enumeration before degrading.
            if (ReadNative(handle, path, buffer, 20, 19, true, token, onBatch)) return;
            if (ReadNative(handle, path, buffer, 11, 10, false, token, onBatch)) return;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        ReadFindFirst(path, token, onBatch);
    }

    private static unsafe bool ReadNative(SafeFileHandle handle, string path, byte[] buffer,
        int restartClass, int nextClass, bool extended, CancellationToken token, Action<List<Entry>> onBatch)
    {
        bool first = true;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            bool ok;
            fixed (byte* pointer = buffer)
                ok = GetFileInformationByHandleEx(handle, first ? restartClass : nextClass,
                    (IntPtr)pointer, BufferSize);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 18) return true; // ERROR_NO_MORE_FILES
                if (first && error is 1 or 50 or 87 or 124) return false;
                throw new Win32Exception(error);
            }
            first = false;
            var entries = new List<Entry>(256);
            int offset = 0;
            while (true)
            {
                int header = extended ? 88 : 104;
                if (offset < 0 || offset > BufferSize - header) throw new InvalidDataException("Invalid directory record.");
                var span = buffer.AsSpan(offset);
                int next = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span));
                int nameBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span[60..]));
                int recordLength = next == 0 ? BufferSize - offset : next;
                if ((nameBytes & 1) != 0 || nameBytes < 0 || recordLength < header ||
                    nameBytes > recordLength - header || recordLength > BufferSize - offset)
                    throw new InvalidDataException("Invalid directory name record.");
                string name = System.Text.Encoding.Unicode.GetString(span.Slice(header, nameBytes));
                if (name is not "." and not "..")
                {
                    var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(span[56..]);
                    var id = extended
                        ? new FileIdentity(BinaryPrimitives.ReadUInt64LittleEndian(span[72..]), BinaryPrimitives.ReadUInt64LittleEndian(span[80..]))
                        : new FileIdentity(BinaryPrimitives.ReadUInt64LittleEndian(span[96..]));
                    entries.Add(Measure(path, name, id,
                        BinaryPrimitives.ReadInt64LittleEndian(span[40..]),
                        BinaryPrimitives.ReadInt64LittleEndian(span[48..]),
                        BinaryPrimitives.ReadInt64LittleEndian(span[24..]), attributes, token));
                }
                if (next == 0) break;
                offset = checked(offset + next);
            }
            if (entries.Count > 0) onBatch(entries);
        }
    }

    private static Entry Measure(string directory, string name, FileIdentity id, long logical, long allocated,
        long modifiedFileTime, FileAttributes attributes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NodeFlags flags = id.IsKnown ? NodeFlags.None : NodeFlags.UnstableIdentity;
        int error = 0;
        if ((attributes & (FileAttributes.ReparsePoint | RemoteData)) != 0)
        {
            allocated = -1;
            if ((attributes & FileAttributes.Directory) != 0) flags |= NodeFlags.ReparseSkipped;
        }
        else if ((attributes & FileAttributes.Directory) != 0) { logical = 0; allocated = 0; }
        else if ((attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            // Only exceptional files need a metadata-only open. Never open cloud/reparse files here.
            using var file = CreateFileW(ExtendedPath(Path.Combine(directory, name)), 0x80, ShareAll,
                IntPtr.Zero, OpenExisting, OpenReparsePoint | OpenNoRecall, IntPtr.Zero);
            if (file.IsInvalid) { error = Marshal.GetLastWin32Error(); allocated = -1; }
            else if (!GetAttributeTagInfo(file, 9, out var opened, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            { error = Marshal.GetLastWin32Error(); allocated = -1; }
            else if (((FileAttributes)opened.Attributes & (FileAttributes.ReparsePoint | RemoteData)) != 0)
                allocated = -1;
            else if (GetFileCompressionInfo(file, 8, out var info, (uint)Marshal.SizeOf<CompressionInfo>()))
                allocated = info.CompressedFileSize < 0 ? -1 : info.CompressedFileSize;
            else { error = Marshal.GetLastWin32Error(); allocated = -1; }
        }
        long ticks = modifiedFileTime >= 0 && modifiedFileTime <= DateTime.MaxValue.Ticks - 504911232000000000L
            ? modifiedFileTime + 504911232000000000L : 0;
        return new(name, id, Math.Max(0, logical), allocated < 0 ? -1 : allocated, ticks, attributes, flags, error);
    }

    private static void ReadFindFirst(string path, CancellationToken token, Action<List<Entry>> onBatch)
    {
        using var find = OpenFind(ExtendedPath(Path.Combine(path, "*")), out var data);
        if (find.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 18) return;
            throw new Win32Exception(error);
        }
        var entries = new List<Entry>(256);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (data.FileName is not "." and not "..")
            {
                long size = ((long)data.SizeHigh << 32) | data.SizeLow;
                // FindFirst has neither allocation sizes nor stable file IDs. Do not invent either.
                var entry = Measure(path, data.FileName, default, size, -1,
                    ((long)data.WriteHigh << 32) | data.WriteLow, (FileAttributes)data.Attributes, token);
                entries.Add(entry);
                if (entries.Count == 256) { onBatch(entries); entries = new(256); }
            }
            if (FindNextFileW(find, out data)) continue;
            int error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error);
            break;
        }
        if (entries.Count > 0) onBatch(entries);
    }

    private static SafeFindHandle OpenFind(string pattern, out FindData data)
    {
        var handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 2);
        if (!handle.IsInvalid || Marshal.GetLastWin32Error() is not (1 or 50 or 87)) return handle;
        handle.Dispose();
        return FindFirstFileExW(pattern, 0, out data, 0, IntPtr.Zero, 0);
    }

    internal static int RecommendedWorkers(string path)
    {
        try
        {
            string root = Path.GetPathRoot(path)!;
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return 4;
            using var volume = CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (volume.IsInvalid) return 2;
            var query = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(query, 7); // StorageDeviceSeekPenaltyProperty
            var answer = new byte[12];
            if (DeviceIoControl(volume, 0x2D1400, query, query.Length, answer, answer.Length, out _, IntPtr.Zero))
                return answer[8] == 0 ? 4 : 2;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or Win32Exception) { }
        return 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompressionInfo { public long CompressedFileSize; public ushort Format; public byte UnitShift, ChunkShift, ClusterShift, Reserved1, Reserved2, Reserved3; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, IntPtr information, int length);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileCompressionInfo(SafeFileHandle handle, int informationClass, out CompressionInfo information, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetAttributeTagInfo(SafeFileHandle handle, int informationClass, out AttributeTagInfo information, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFileAttributesW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFindHandle FindFirstFileExW(string pattern, int infoLevel, out FindData data, int searchOp, IntPtr filter, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextFileW(SafeFindHandle handle, out FindData data);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
