using System.Runtime.CompilerServices;
using System.IO;
using System.Security.Cryptography;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

[assembly: InternalsVisibleTo("DiskPeek.Tests")]

namespace DiskPeek.App;

internal interface IUpdateBackend
{
    bool IsInstalled { get; }
    SemanticVersion? CurrentVersion { get; }
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(UpdateInfo update, Action<int>? progress, CancellationToken cancellationToken);
    void ApplyAndRestart(UpdateInfo update);
}

internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    private readonly IVelopackLocator locator;
    private readonly UpdateManager manager;
    public VelopackUpdateBackend()
    {
        locator = VelopackLocator.Current;
        manager = new UpdateManager(new GithubSource("https://github.com/GoByeBye/DiskPeek", accessToken: null, prerelease: false),
            new UpdateOptions { ExplicitChannel = "win-x64", AllowVersionDowngrade = false }, locator);
    }
    private Task<UpdateInfo?>? pendingCheck;
    public bool IsInstalled => manager.IsInstalled && !manager.IsPortable;
    public SemanticVersion? CurrentVersion => manager.CurrentVersion;
    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        // The SDK's check has no cancellation parameter. Reuse an in-flight request
        // after a UI timeout instead of starting overlapping GitHub requests.
        var task = pendingCheck ??= manager.CheckForUpdatesAsync();
        try { return await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { if (task.IsCompleted) pendingCheck = null; }
    }
    public async Task DownloadAsync(UpdateInfo update, Action<int>? progress, CancellationToken cancellationToken)
    {
        string targetPath = PackagePath(update.TargetFullRelease);
        await manager.DownloadUpdatesAsync(update, progress, cancellationToken).ConfigureAwait(false);
        await VerifyPackageAsync(targetPath, update.TargetFullRelease, cancellationToken).ConfigureAwait(false);
    }
    internal static async Task VerifyPackageAsync(string path, VelopackAsset asset, CancellationToken cancellationToken)
    {
        if (asset.SHA256 is not { Length: 64 } || asset.Size <= 0) throw new InvalidDataException("Update feed is missing a valid SHA-256 or package size.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != asset.Size) throw new InvalidDataException("Downloaded update size does not match its feed.");
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(hash).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded update failed SHA-256 verification.");
    }
    public void ApplyAndRestart(UpdateInfo update)
    {
        VerifyPackageAsync(PackagePath(update.TargetFullRelease), update.TargetFullRelease, CancellationToken.None).GetAwaiter().GetResult();
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease, []);
    }
    private string PackagePath(VelopackAsset asset)
    {
        string name = asset.FileName;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update feed contains an invalid package filename.");
        return Path.Combine(locator.PackagesDir ?? throw new InvalidOperationException("Installed package storage is unavailable."), name);
    }
}

internal sealed class UpdateService(IUpdateBackend backend)
{
    private readonly SemaphoreSlim operation = new(1, 1);
    public bool IsInstalled => backend.IsInstalled;
    public SemanticVersion? CurrentVersion => backend.CurrentVersion;
    public UpdateInfo? Available { get; private set; }
    public bool DownloadReady { get; private set; }
    public static bool ShouldCheck(bool enabled, DateTime? lastUtc, DateTime nowUtc) => enabled &&
        (!lastUtc.HasValue || lastUtc.Value > nowUtc.AddMinutes(5) || nowUtc - lastUtc.Value >= TimeSpan.FromDays(1));

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsInstalled) return null;
            var candidate = await backend.CheckAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate != null && (candidate.IsDowngrade || candidate.TargetFullRelease.Version.IsPrerelease ||
                CurrentVersion == null || candidate.TargetFullRelease.Version <= CurrentVersion)) candidate = null;
            bool same = candidate != null && Available != null && candidate.TargetFullRelease.Version == Available.TargetFullRelease.Version &&
                candidate.TargetFullRelease.FileName == Available.TargetFullRelease.FileName &&
                candidate.TargetFullRelease.Size == Available.TargetFullRelease.Size &&
                string.Equals(candidate.TargetFullRelease.SHA256, Available.TargetFullRelease.SHA256, StringComparison.OrdinalIgnoreCase);
            Available = candidate;
            if (!same) DownloadReady = false;
            return candidate;
        }
        finally { operation.Release(); }
    }

    public async Task DownloadAsync(Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInstalled || Available == null) throw new InvalidOperationException("No installable update is available.");
            DownloadReady = false;
            await backend.DownloadAsync(Available, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            DownloadReady = true;
        }
        finally { operation.Release(); }
    }

    public void Apply(bool isScanning, bool isExporting, bool confirmed)
    {
        if (!operation.Wait(0)) throw new InvalidOperationException("An update operation is still running.");
        try
        {
            if (!confirmed) throw new InvalidOperationException("Restart confirmation is required.");
            if (isScanning || isExporting) throw new InvalidOperationException("Finish or stop the scan and wait for exports before updating.");
            if (!IsInstalled || Available == null || !DownloadReady) throw new InvalidOperationException("Download the update before applying it.");
            backend.ApplyAndRestart(Available);
        }
        finally { operation.Release(); }
    }
}
