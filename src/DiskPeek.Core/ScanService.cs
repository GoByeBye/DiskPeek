namespace DiskPeek.Core;

public static class ScanService
{
    public static async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string target = Path.GetFullPath(request.Target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"Scan target is not an accessible directory: {target}");
        request = request with { Target = target };
        IScanEngine engine = request.Engine switch
        {
            ScanEngineKind.Mft => new MftScanEngine(),
            ScanEngineKind.Baseline => new BaselineScanEngine(),
            _ => new DirectoryScanEngine()
        };
        try
        {
            var result = await engine.ScanAsync(request, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(result.Count, 0, result.Elapsed, "Aggregating", result.Engine));
            return ScanAggregation.Complete(result);
        }
        catch (Exception ex) when (request.Engine == ScanEngineKind.Mft && ex is not OperationCanceledException && ex is IOException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            progress?.Report(new(0, 0, TimeSpan.Zero, "MFT unavailable; using directory scan", "Directory"));
            var result = await new DirectoryScanEngine().ScanAsync(request with { Engine = ScanEngineKind.Directory }, progress, cancellationToken).ConfigureAwait(false);
            result.Issues.Insert(0, new(target, $"MFT scan unavailable; directory fallback used: {ex.Message}"));
            return ScanAggregation.Complete(result);
        }
    }
}
