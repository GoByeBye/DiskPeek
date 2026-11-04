using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

internal static class Program
{
public static int Main(string[] args)
{
    VelopackApp.Build().SetArgs(args).SetLogger(new QuietLogger()).SetAutoApplyOnStartup(false).Run();
    return RunAsync(args).GetAwaiter().GetResult();
}
private static async Task<int> RunAsync(string[] args)
{
string output = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "probe.json");
string command = args.Length > 0 ? args[0] : "probe";
string feed = args.Length > 2 ? args[2] : AppContext.BaseDirectory;
var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(feed)),
    new UpdateOptions { ExplicitChannel = "win-x64", AllowVersionDowngrade = false });
var report = new Dictionary<string, object?>
{
    ["Command"] = command, ["IsInstalled"] = manager.IsInstalled, ["IsPortable"] = manager.IsPortable,
    ["CurrentVersion"] = manager.CurrentVersion?.ToString(), ["PackageId"] = manager.AppId,
    ["AssemblyVersion"] = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3),
    ["AutoApplyOnStartup"] = false
};
int exitCode = 0;
try
{
    if (command == "apply-and-restart")
    {
        var pending = manager.UpdatePendingRestart ?? throw new InvalidOperationException("No staged update is ready.");
        // This is the exact production SDK entry point. The restarted console probe
        // writes fresh version evidence and exits; no WPF application is launched.
        manager.ApplyUpdatesAndRestart(pending, ["probe", output, feed]);
        throw new InvalidOperationException("SDK apply unexpectedly returned without exiting.");
    }
    if (command is "check" or "stage")
    {
        var candidate = await manager.CheckForUpdatesAsync();
        report["AvailableVersion"] = candidate?.TargetFullRelease.Version.ToString();
        report["AvailableFile"] = candidate?.TargetFullRelease.FileName;
        if (command == "stage" && candidate != null)
        {
            await manager.DownloadUpdatesAsync(candidate);
            report["DownloadComplete"] = true;
        }
    }
    report["PendingVersion"] = manager.UpdatePendingRestart?.Version.ToString();
}
catch (Exception exception)
{
    report["ErrorType"] = exception.GetType().FullName;
    report["Error"] = exception.Message;
    exitCode = 3;
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return exitCode;
}
}

internal sealed class QuietLogger : IVelopackLogger
{
    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception) { }
}
