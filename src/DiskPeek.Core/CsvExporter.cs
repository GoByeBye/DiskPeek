using System.Globalization;
using System.Text;

namespace DiskPeek.Core;

/// <summary>Exports the observed scan, including incomplete and shared-allocation status.</summary>
public static class CsvExporter
{
    public static async Task ExportAsync(ScanResult result, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.LogicalTotals.Length != result.Count) throw new InvalidOperationException("Aggregate the scan before export.");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024);
        await writer.WriteLineAsync("Path,Kind,LogicalBytes,AllocatedFileDataBytes,AllocatedTotalComplete,SharedAllocation,UnmeasuredDescendants,Attributes,ModifiedUtc,Engine,ScanCompletedUtc,ScanPartial,ScanCancelled,ScopeNote,ObservedFileAllocationBytes,NodeFlags,HardLinkAccountingAvailable,SkippedEntries,VolumeCapacityBytes,VolumeFreeBytes".AsMemory(), cancellationToken);
        for (var index = 0; index < result.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = result.Nodes[index];
            var measured = node.IsDirectory || node.AllocatedBytes >= 0;
            var complete = measured && result.UnknownCounts[index] == 0 && (node.Flags & NodeFlags.Inaccessible) == 0;
            string modified = node.ModifiedUtcTicks > 0 && node.ModifiedUtcTicks <= DateTime.MaxValue.Ticks
                ? new DateTime(node.ModifiedUtcTicks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : "";
            var fields = new[]
            {
                result.GetPath(index), node.IsDirectory ? "Directory" : "File",
                result.LogicalTotals[index].ToString(CultureInfo.InvariantCulture),
                measured ? result.AllocatedTotals[index].ToString(CultureInfo.InvariantCulture) : "",
                complete.ToString(), ((node.Flags & NodeFlags.SharedAllocation) != 0).ToString(),
                result.UnknownCounts[index].ToString(CultureInfo.InvariantCulture), node.Attributes.ToString(), modified,
                result.Engine, result.CompletedUtc.ToString("O", CultureInfo.InvariantCulture), result.Partial.ToString(),
                result.Cancelled.ToString(), "Unnamed file data only; excludes alternate streams and filesystem overhead; live observation",
                !node.IsDirectory && node.AllocatedBytes >= 0 ? node.AllocatedBytes.ToString(CultureInfo.InvariantCulture) : "",
                node.Flags.ToString(), result.HardLinkAccountingAvailable.ToString(), result.SkippedEntries.ToString(CultureInfo.InvariantCulture),
                result.VolumeCapacity?.ToString(CultureInfo.InvariantCulture) ?? "", result.VolumeFree?.ToString(CultureInfo.InvariantCulture) ?? ""
            };
            await writer.WriteLineAsync(string.Join(',', fields.Select(Quote)).AsMemory(), cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
