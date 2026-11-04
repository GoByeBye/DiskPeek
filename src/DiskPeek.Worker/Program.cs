using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using DiskPeek.Core;

return await WorkerHost.RunAsync(args);

internal static class WorkerHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        // Narrow command line: no arbitrary scan modes, output paths, or shell commands.
        if (args.Length != 6 || args[0] != "--pipe" || args[2] != "--token" || args[4] != "--parent" ||
            !args[1].StartsWith("DiskPeek-", StringComparison.Ordinal) || args[1].Length != 41 ||
            !Guid.TryParseExact(args[1][9..], "N", out _) || args[3].Length != 64 ||
            !int.TryParse(args[5], out int parentPid) || parentPid <= 0) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await pipe.ConnectAsync(connectionTimeout.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != parentPid) return 4;
            using var writer = new WorkerProtocol.Writer(pipe);
            await writer.SendAsync(WorkerProtocol.Kind.Hello, data =>
            { data.Write(WorkerProtocol.Version); WorkerProtocol.WriteText(data, args[3]); });
            var frame = await WorkerProtocol.ReceiveAsync(pipe, connectionTimeout.Token);
            if (frame.Kind != WorkerProtocol.Kind.Request) return 5;
            string target;
            int workers;
            using (var reader = frame.Reader())
            {
                if (reader.ReadInt32() != WorkerProtocol.Version) return 5;
                target = WorkerProtocol.ReadText(reader); workers = reader.ReadInt32();
            }
            if (!WorkerClient.IsSupportedTarget(target))
            { await writer.SendAsync(WorkerProtocol.Kind.Error, data => WorkerProtocol.WriteText(data, "Only local NTFS drive roots are allowed.")); return 6; }
            using var cancelled = new CancellationTokenSource();
            using var stopMonitor = new CancellationTokenSource();
            var monitor = Task.Run(async () =>
            {
                try
                {
                    var control = await WorkerProtocol.ReceiveAsync(pipe, stopMonitor.Token);
                    if (control.Kind == WorkerProtocol.Kind.Cancel) cancelled.Cancel();
                    else cancelled.Cancel();
                }
                catch (OperationCanceledException) { }
                catch (IOException) { cancelled.Cancel(); }
            });
            // Synchronous progress adapter keeps messages ordered and avoids late Progress callbacks after Complete.
            var progress = new DirectProgress(value =>
            {
                try { writer.SendAsync(WorkerProtocol.Kind.Progress, data => WorkerProtocol.WriteProgress(data, value)).GetAwaiter().GetResult(); }
                catch (IOException) { cancelled.Cancel(); }
            });
            try
            {
                var result = await ScanService.ScanAsync(new(target, ScanEngineKind.Mft, Math.Clamp(workers, 0, 16)), progress, cancelled.Token);
                for (int offset = 0; offset < result.Count; offset += 1024)
                {
                    int first = offset, count = Math.Min(1024, result.Count - offset);
                    await writer.SendAsync(WorkerProtocol.Kind.Nodes, data =>
                    { data.Write(count); for (int i = 0; i < count; i++) WorkerProtocol.WriteNode(data, result.Nodes[first + i]); });
                }
                await writer.SendAsync(WorkerProtocol.Kind.Complete, data => WorkerProtocol.WriteCompletion(data, result));
                return 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                await writer.SendAsync(WorkerProtocol.Kind.Error, data => WorkerProtocol.WriteText(data, ex.Message));
                return 7;
            }
            finally { stopMonitor.Cancel(); await monitor; }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return 8; }
    }
    private sealed class DirectProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => report(value); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
