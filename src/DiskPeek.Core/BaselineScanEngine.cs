using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskPeek.Core;

/// <summary>A deliberately straightforward per-entry metadata walker for matched benchmarks.</summary>
public sealed class BaselineScanEngine : IScanEngine
{
    public string Name => "Managed directory baseline";

    public Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Scan(request, progress, cancellationToken), CancellationToken.None);

    private ScanResult Scan(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var target = Path.GetFullPath(request.Target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        var nodes = new List<ScanNode>(4096);
        var issues = new List<ScanIssue>();
        var skipped = 0L;
        var directories = 0L;
        var lastProgress = -100L;
        var root = ReadNode(target, -1, Path.GetFileName(Path.TrimEndingDirectorySeparator(target)), FileAttributes.Directory, issues, ref skipped);
        nodes.Add(root);
        var pending = new Stack<(string Path, int Index)>();
        pending.Push((target, 0));
        while (pending.Count != 0 && !ct.IsCancellationRequested)
        {
            var directory = pending.Pop();
            directories++;
            try
            {
                foreach (var entry in new DirectoryInfo(directory.Path).EnumerateFileSystemInfos())
                {
                    if (ct.IsCancellationRequested) break;
                    FileAttributes attributes;
                    try { attributes = entry.Attributes; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        RecordIssue(entry.FullName, ex.Message, issues, ref skipped);
                        continue;
                    }
                    var index = nodes.Count;
                    var node = ReadNode(entry.FullName, directory.Index, entry.Name, attributes, issues, ref skipped);
                    nodes.Add(node);
                    if (node.IsDirectory && (attributes & FileAttributes.ReparsePoint) == 0 && (node.Flags & NodeFlags.Inaccessible) == 0)
                        pending.Push((entry.FullName, index));
                    if (clock.ElapsedMilliseconds - lastProgress >= 100)
                    {
                        progress?.Report(new(nodes.Count, directories, clock.Elapsed, "Enumerating", Name));
                        lastProgress = clock.ElapsedMilliseconds;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                var failed = nodes[directory.Index];
                failed.Flags |= NodeFlags.Inaccessible;
                nodes[directory.Index] = failed;
                RecordIssue(directory.Path, ex.Message, issues, ref skipped);
            }
        }
        long? capacity = null, free = null;
        try { var drive = new DriveInfo(Path.GetPathRoot(target)!); capacity = drive.TotalSize; free = drive.AvailableFreeSpace; } catch { }
        clock.Stop();
        progress?.Report(new(nodes.Count, directories, clock.Elapsed, ct.IsCancellationRequested ? "Cancelled" : "Complete", Name));
        return new ScanResult
        {
            Target = target, Nodes = nodes.ToArray(), Engine = Name, Elapsed = clock.Elapsed,
            Cancelled = ct.IsCancellationRequested, Partial = ct.IsCancellationRequested || skipped > 0,
            Issues = issues, SkippedEntries = skipped, VolumeCapacity = capacity, VolumeFree = free,
            HardLinkAccountingAvailable = nodes.Where(n => !n.IsDirectory).All(n => n.Identity.IsKnown)
        };
    }

    private static ScanNode ReadNode(string path, int parent, string name, FileAttributes attributes, List<ScanIssue> issues, ref long skipped)
    {
        var node = new ScanNode { Name = name, ParentIndex = parent, Attributes = attributes, AllocatedBytes = -1 };
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            node.Flags = NodeFlags.ReparseSkipped;
            try { if (!node.IsDirectory) node.LogicalBytes = new FileInfo(path).Length; } catch { }
            return node;
        }
        using var handle = CreateFileW(ExtendedPath(path), 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            node.Flags = NodeFlags.Inaccessible;
            RecordIssue(path, new Win32Exception(Marshal.GetLastWin32Error()).Message, issues, ref skipped);
            return node;
        }
        if (GetFileInformationByHandleEx(handle, 0, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            node.Attributes = (FileAttributes)basic.Attributes;
            if (basic.LastWriteTime > 0) node.ModifiedUtcTicks = DateTime.FromFileTimeUtc(basic.LastWriteTime).Ticks;
        }
        if (GetFileInformationByHandleEx(handle, 1, out FileStandardInfo standard, (uint)Marshal.SizeOf<FileStandardInfo>()))
        {
            node.LogicalBytes = node.IsDirectory ? 0 : Math.Max(0, standard.EndOfFile);
            node.AllocatedBytes = node.IsDirectory ? 0 : Math.Max(0, standard.AllocationSize);
        }
        else
        {
            node.Flags |= NodeFlags.Inaccessible;
            RecordIssue(path, new Win32Exception(Marshal.GetLastWin32Error()).Message, issues, ref skipped);
        }
        if (GetFileInformationByHandleEx(handle, 18, out FileIdInfo id, (uint)Marshal.SizeOf<FileIdInfo>()))
            node.Identity = new(id.Low, id.High);
        else node.Flags |= NodeFlags.UnstableIdentity;
        if (!node.IsDirectory && (attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            if (GetFileInformationByHandleEx(handle, 8, out FileCompressionInfo compression, (uint)Marshal.SizeOf<FileCompressionInfo>()))
                node.AllocatedBytes = Math.Max(0, compression.CompressedFileSize);
            else node.AllocatedBytes = -1;
        }
        return node;
    }

    private static void RecordIssue(string path, string message, List<ScanIssue> issues, ref long skipped)
    {
        skipped++;
        if (issues.Count < 1000) issues.Add(new(path, message));
    }

    private static string ExtendedPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + Path.GetFullPath(path);

    [StructLayout(LayoutKind.Sequential)] private struct FileBasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct FileStandardInfo { public long AllocationSize, EndOfFile; public uint NumberOfLinks; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong VolumeSerial, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct FileCompressionInfo { public long CompressedFileSize; public ushort CompressionFormat; public byte CompressionUnitShift, ChunkShift, ClusterShift, Reserved1, Reserved2, Reserved3; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileBasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileStandardInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileCompressionInfo info, uint size);
}
