using DiskPeek.App;
using Velopack;

internal static partial class Verification
{
    private sealed class FakeUpdateBackend : IUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;
        public SemanticVersion? CurrentVersion { get; set; } = SemanticVersion.Parse("0.2.0");
        public UpdateInfo? Candidate { get; set; } = NewUpdate("0.2.1");
        public int Checks, Downloads, Applies;
        public Func<CancellationToken, Task<UpdateInfo?>>? Check;
        public Func<CancellationToken, Task>? Download;
        public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return Check?.Invoke(cancellationToken) ?? Task.FromResult(Candidate);
        }
        public async Task DownloadAsync(UpdateInfo update, Action<int>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            if (Download != null) await Download(cancellationToken);
            progress?.Invoke(100);
        }
        public void ApplyAndRestart(UpdateInfo update) => Applies++;
    }

    private static UpdateInfo NewUpdate(string version, bool downgrade = false) => new(new VelopackAsset
    {
        PackageId = "DiskPeek", Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full,
        FileName = "DiskPeek-" + version + "-full.nupkg", SHA256 = new string('a', 64), Size = 1024,
        NotesMarkdown = "Test release"
    }, downgrade);

    private static async Task ThrowsUpdate<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void RejectApply(UpdateService service, bool scan = false, bool export = false, bool confirmed = true)
    {
        try { service.Apply(scan, export, confirmed); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Unsafe update application was permitted.");
    }

    private static async Task TestUpdateConsent()
    {
        var backend = new FakeUpdateBackend();
        var service = new UpdateService(backend);
        RejectApply(service);
        var available = await service.CheckAsync();
        Assert(available?.TargetFullRelease.Version == SemanticVersion.Parse("0.2.1"), "New stable update was not offered.");
        Assert(backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0 && !service.DownloadReady,
            "Checking performed a download or application.");
        RejectApply(service);
        await service.DownloadAsync();
        Assert(service.DownloadReady && backend.Downloads == 1 && backend.Applies == 0, "Download auto-applied the package.");
        RejectApply(service, confirmed: false); RejectApply(service, scan: true); RejectApply(service, export: true);
        Assert(backend.Applies == 0, "Apply bypassed user consent or active-work protection.");
        service.Apply(false, false, true);
        Assert(backend.Applies == 1, "An explicitly confirmed idle update was not applied.");
    }

    private static async Task TestPortableUpdates()
    {
        var backend = new FakeUpdateBackend { IsInstalled = false };
        var service = new UpdateService(backend);
        Assert(await service.CheckAsync() == null && backend.Checks == 0, "Portable mode accessed the release feed.");
        await ThrowsUpdate<InvalidOperationException>(() => service.DownloadAsync());
        RejectApply(service);
        Assert(backend.Downloads == 0 && backend.Applies == 0, "Portable mode modified installation state.");
    }

    private static async Task TestUpdateVersions()
    {
        foreach (var candidate in new[] { NewUpdate("0.1.9"), NewUpdate("0.2.0"), NewUpdate("0.3.0-preview.1"), NewUpdate("0.3.0", downgrade: true) })
        {
            var backend = new FakeUpdateBackend();
            var service = new UpdateService(backend);
            await service.CheckAsync(); await service.DownloadAsync();
            backend.Candidate = candidate;
            Assert(await service.CheckAsync() == null && service.Available == null && !service.DownloadReady,
                "An older, equal, prerelease or downgrade offer stayed installable.");
            RejectApply(service);
            Assert(backend.Applies == 0, "A rejected offer was applied.");
        }
        var noVersion = new FakeUpdateBackend { CurrentVersion = null };
        Assert(await new UpdateService(noVersion).CheckAsync() == null, "An unknown installed version allowed an update.");
        var changedBackend = new FakeUpdateBackend();
        var changed = new UpdateService(changedBackend);
        await changed.CheckAsync(); await changed.DownloadAsync();
        await changed.CheckAsync();
        Assert(changed.DownloadReady, "Unchanged release metadata discarded a prepared update.");
        changedBackend.Candidate = new UpdateInfo(changedBackend.Candidate!.TargetFullRelease with { SHA256 = new string('b', 64) }, false);
        await changed.CheckAsync();
        Assert(!changed.DownloadReady, "Changed package metadata reused the old download readiness.");
    }

    private static async Task TestUpdatePayload()
    {
        string root = NewFixture("update-payload"), path = Path.Combine(root, "payload.nupkg");
        byte[] bytes = Enumerable.Range(0, 8192).Select(i => (byte)(i * 31)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var asset = NewUpdate("0.2.1").TargetFullRelease with
        { Size = bytes.Length, SHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) };
        await VelopackUpdateBackend.VerifyPackageAsync(path, asset, CancellationToken.None);
        await ThrowsUpdate<InvalidDataException>(() => VelopackUpdateBackend.VerifyPackageAsync(path, asset with { Size = bytes.Length + 1 }, CancellationToken.None));
        await ThrowsUpdate<InvalidDataException>(() => VelopackUpdateBackend.VerifyPackageAsync(path, asset with { SHA256 = "" }, CancellationToken.None));
        bytes[1024] ^= 0x80;
        await File.WriteAllBytesAsync(path, bytes);
        await ThrowsUpdate<InvalidDataException>(() => VelopackUpdateBackend.VerifyPackageAsync(path, asset, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsUpdate<OperationCanceledException>(() => VelopackUpdateBackend.VerifyPackageAsync(path, asset, cancelled.Token));
    }

    private static async Task TestUpdateFailures()
    {
        var backend = new FakeUpdateBackend();
        var service = new UpdateService(backend);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsUpdate<OperationCanceledException>(() => service.CheckAsync(cancelled.Token));
        Assert(backend.Checks == 0, "A cancelled check started network activity.");
        backend.Check = _ => Task.FromException<UpdateInfo?>(new HttpRequestException("Offline fixture"));
        await ThrowsUpdate<HttpRequestException>(() => service.CheckAsync());
        Assert(service.Available == null && !service.DownloadReady && backend.Applies == 0, "Offline failure enabled installation.");
        backend.Check = null;
        await service.CheckAsync();
        using var downloadCancel = new CancellationTokenSource();
        backend.Download = token => { downloadCancel.Cancel(); return Task.CompletedTask; };
        await ThrowsUpdate<OperationCanceledException>(() => service.DownloadAsync(cancellationToken: downloadCancel.Token));
        Assert(!service.DownloadReady && backend.Applies == 0, "Cancelled download remained installable.");
        RejectApply(service);
        backend.Download = _ => Task.FromException(new InvalidDataException("Checksum fixture"));
        await ThrowsUpdate<InvalidDataException>(() => service.DownloadAsync());
        Assert(!service.DownloadReady, "Integrity failure remained installable.");
        backend.Download = null;
        await service.DownloadAsync();
        Assert(service.DownloadReady && backend.Applies == 0, "Download retry failed or auto-applied.");
    }

    private static async Task TestUpdateConcurrency()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<UpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeUpdateBackend { Check = _ => { entered.TrySetResult(); return finish.Task; } };
        var service = new UpdateService(backend);
        var first = service.CheckAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        RejectApply(service);
        using var cancellation = new CancellationTokenSource();
        var queued = service.DownloadAsync(cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await ThrowsUpdate<OperationCanceledException>(() => queued);
        finish.SetResult(NewUpdate("0.2.1"));
        await first;
        Assert(backend.Checks == 1 && backend.Downloads == 0 && backend.Applies == 0, "Concurrent update actions escaped serialization.");
    }

    private static Task TestUpdateSchedule()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert(!UpdateService.ShouldCheck(false, null, now), "Opt-out still scheduled a request.");
        Assert(UpdateService.ShouldCheck(true, null, now), "First-run check was suppressed.");
        Assert(!UpdateService.ShouldCheck(true, now.AddHours(-23), now), "Repeated background request within one day.");
        Assert(UpdateService.ShouldCheck(true, now.AddDays(-1), now), "Daily check was not due.");
        Assert(UpdateService.ShouldCheck(true, now.AddHours(1), now), "Future clock skew permanently suppressed updates.");
        return Done();
    }

    private static Task TestUserSettings()
    {
        string root = NewFixture("update-settings"), path = Path.Combine(root, "nested", "settings.json");
        var initial = new SettingsStore(path);
        initial.Settings.Theme = "Dark"; initial.Settings.AutomaticUpdateChecks = false;
        initial.Settings.LastUpdateCheckUtc = new DateTime(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc);
        Assert(initial.TrySave(out _) && File.Exists(path), "Preferences were not written to the injected path.");
        var loaded = new SettingsStore(path);
        Assert(loaded.Settings.Theme == "Dark" && !loaded.Settings.AutomaticUpdateChecks &&
            loaded.Settings.LastUpdateCheckUtc == initial.Settings.LastUpdateCheckUtc, "Theme/update preferences did not persist.");
        Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Atomic-write temporary file was left behind.");
        File.WriteAllText(path, "{broken-json");
        var malformed = new SettingsStore(path);
        Assert(malformed.LoadError != null && malformed.Settings.Theme == "System", "Malformed preferences were not recovered.");
        File.WriteAllText(path, new string('x', 65537));
        Assert(new SettingsStore(path).LoadError != null, "Oversized preferences were parsed.");
        string blocking = Path.Combine(root, "blocking-file"); File.WriteAllText(blocking, "occupied");
        Assert(!new SettingsStore(Path.Combine(blocking, "settings.json")).TrySave(out var error) && error != null,
            "An unavailable settings directory did not report a recoverable save failure.");
        return Done();
    }
}
