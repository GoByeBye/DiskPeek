using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace DiskPeek.Core;

public static class WorkerClient
{
    public static bool IsSupportedTarget(string target)
    {
        try
        {
            string full = Path.GetFullPath(target);
            if (full.Length != 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\') return false;
            var drive = new DriveInfo(full);
            return drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable &&
                drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(request.Target);
        if (!IsSupportedTarget(target)) throw new ArgumentException("The elevated scanner only accepts local NTFS drive roots.");
        string workerPath = Path.Combine(AppContext.BaseDirectory, "DiskPeek.Worker.exe");
        if (!File.Exists(workerPath)) throw new FileNotFoundException("DiskPeek.Worker.exe must be beside DiskPeek.exe.", workerPath);
        string pipeName = "DiskPeek-" + Guid.NewGuid().ToString("N");
        string secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
        using var processHandle = LaunchElevated(workerPath, $"--pipe {pipeName} --token {secret} --parent {Environment.ProcessId}");
        int pid = checked((int)GetProcessId(processHandle));
        if (pid <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var worker = Process.GetProcessById(pid);
        using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var connection = pipe.WaitForConnectionAsync(connectionTimeout.Token);
            var exited = worker.WaitForExitAsync();
            if (await Task.WhenAny(connection, exited).ConfigureAwait(false) != connection)
                throw new IOException("The elevated scanner exited before connecting.");
            await connection.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != pid)
                throw new SecurityException("Elevated scanner process verification failed.");
            using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var hello = await WorkerProtocol.ReceiveAsync(pipe, handshakeTimeout.Token).ConfigureAwait(false);
            using (var reader = hello.Reader())
                if (hello.Kind != WorkerProtocol.Kind.Hello || reader.ReadInt32() != WorkerProtocol.Version ||
                    !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(WorkerProtocol.ReadText(reader)), System.Text.Encoding.UTF8.GetBytes(secret)))
                    throw new SecurityException("Elevated scanner handshake failed.");
            using var writer = new WorkerProtocol.Writer(pipe);
            await writer.SendAsync(WorkerProtocol.Kind.Request, data =>
            { data.Write(WorkerProtocol.Version); WorkerProtocol.WriteText(data, target); data.Write(Math.Clamp(request.WorkerCount, 0, 16)); }).ConfigureAwait(false);

            using var stopReading = new CancellationTokenSource();
            Task? cancelWrite = null;
            using var registration = cancellationToken.Register(() =>
            {
                // Continue receiving after cancellation so the worker can return a partial scan.
                cancelWrite = SendCancel();
            });
            async Task SendCancel()
            {
                stopReading.CancelAfter(TimeSpan.FromSeconds(10));
                try { await writer.SendAsync(WorkerProtocol.Kind.Cancel).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }
            var nodes = new List<ScanNode>(65536);
            while (true)
            {
                var frame = await WorkerProtocol.ReceiveAsync(pipe, stopReading.Token).ConfigureAwait(false);
                using var reader = frame.Reader();
                switch (frame.Kind)
                {
                    case WorkerProtocol.Kind.Progress: progress?.Report(WorkerProtocol.ReadProgress(reader)); break;
                    case WorkerProtocol.Kind.Nodes:
                        int count = reader.ReadInt32();
                        if (count < 0 || count > 1024) throw new InvalidDataException("Invalid node batch size.");
                        for (int i = 0; i < count; i++) nodes.Add(WorkerProtocol.ReadNode(reader));
                        break;
                    case WorkerProtocol.Kind.Complete:
                        var result = WorkerProtocol.ReadCompletion(reader, nodes, target);
                        registration.Dispose();
                        if (cancelWrite is not null) await cancelWrite.ConfigureAwait(false);
                        return ScanAggregation.Complete(result);
                    case WorkerProtocol.Kind.Error: throw new IOException(WorkerProtocol.ReadText(reader));
                    default: throw new InvalidDataException("Unexpected elevated scanner response.");
                }
            }
        }
        finally
        {
            // This is our own read-only worker; ensure a disconnected or stuck helper does not outlive the request.
            try
            {
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await worker.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { try { worker.Kill(); } catch (InvalidOperationException) { } }
        }
    }

    private static SafeProcessHandle LaunchElevated(string executable, string arguments)
    {
        var info = new ShellExecuteInfo { Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x40 | 0x100,
            Verb = "runas", File = executable, Parameters = arguments, Directory = AppContext.BaseDirectory, Show = 0 };
        if (!ShellExecuteExW(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (info.Process == IntPtr.Zero) throw new IOException("Windows did not return the elevated scanner process.");
        return new SafeProcessHandle(info.Process, true);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show; public IntPtr Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey; public uint HotKey; public IntPtr IconOrMonitor, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteExW(ref ShellExecuteInfo information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
