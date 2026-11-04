using System.Diagnostics;

namespace DiskPeek.Core;

public enum ScanEngineKind { Auto, Directory, Mft, Baseline }
public enum SizeMetric { Allocated, Logical }
[Flags]
public enum NodeFlags { None = 0, SharedAllocation = 1, Inaccessible = 2, ReparseSkipped = 4, UnstableIdentity = 8, AdditionalStreams = 16 }
public readonly record struct FileIdentity(ulong Low, ulong High = 0)
{
    public bool IsKnown => Low != 0 || High != 0;
}
public struct ScanNode
{
    public string Name;
    public int ParentIndex;
    public FileIdentity Identity;
    public long LogicalBytes;
    // -1 means unmeasured. Zero is a measured empty/resident stream.
    public long AllocatedBytes;
    public long ModifiedUtcTicks;
    public FileAttributes Attributes;
    public NodeFlags Flags;
    public readonly bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}
public sealed record ScanRequest(string Target, ScanEngineKind Engine = ScanEngineKind.Auto, int WorkerCount = 0);
public sealed record ScanProgress(long Entries, long Directories, TimeSpan Elapsed, string Phase, string Engine);
public sealed record ScanIssue(string Path, string Message);
public interface IScanEngine
{
    string Name { get; }
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}
public sealed class ScanResult
{
    public required string Target { get; init; }
    public required ScanNode[] Nodes { get; init; }
    public required string Engine { get; init; }
    public TimeSpan Elapsed { get; init; }
    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;
    public bool Cancelled { get; init; }
    public bool Partial { get; set; }
    public bool HardLinkAccountingAvailable { get; set; } = true;
    public List<ScanIssue> Issues { get; init; } = [];
    public long SkippedEntries { get; init; }
    public long? VolumeCapacity { get; init; }
    public long? VolumeFree { get; init; }
    public long[] LogicalTotals { get; internal set; } = [];
    public long[] AllocatedTotals { get; internal set; } = [];
    public int[] FileCounts { get; internal set; } = [];
    public int[] DirectoryCounts { get; internal set; } = [];
    public int[] UnknownCounts { get; internal set; } = [];
    public int[] FirstChild { get; internal set; } = [];
    public int[] NextSibling { get; internal set; } = [];
    public int Count => Nodes.Length;
    public long Value(int index, SizeMetric metric) => metric == SizeMetric.Logical ? LogicalTotals[index] : AllocatedTotals[index];
    public string GetPath(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == 0) return Target;
        var parts = new List<string>();
        var steps = 0;
        while (index > 0 && steps++ < Count)
        {
            parts.Add(Nodes[index].Name);
            index = Nodes[index].ParentIndex;
        }
        if (steps >= Count) throw new InvalidDataException("Cyclic scan path");
        parts.Reverse();
        return Path.Combine([Target, .. parts]);
    }
    public IEnumerable<int> Children(int index)
    {
        for (var child = FirstChild[index]; child >= 0; child = NextSibling[child]) yield return child;
    }
    public bool IsDescendantOf(int index, int root)
    {
        for (int steps = 0; index >= 0 && steps <= Count; steps++, index = Nodes[index].ParentIndex)
            if (index == root) return true;
        return false;
    }
}
public static class FormatUtil
{
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }
}
