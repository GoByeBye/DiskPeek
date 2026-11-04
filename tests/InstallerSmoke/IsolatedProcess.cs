using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskPeekSmoke
{
    // Test isolation only: this desktop is never switched to or used for input.
    public sealed class IsolatedProcess : IDisposable
    {
        private IntPtr desktop;
        private IntPtr process;
        private IsolatedProcess(IntPtr desktop, IntPtr process) { this.desktop = desktop; this.process = process; }
        public static IsolatedProcess Start(string executable, string[] arguments, string workingDirectory)
        {
            var values = new[] { executable }.Concat(arguments).ToArray();
            if (values.Any(value => value.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0))
                throw new ArgumentException("Unexpected quote or newline in test process arguments.");
            string name = "DiskPeekSmoke" + Guid.NewGuid().ToString("N");
            IntPtr desktop = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), desktop = name, flags = 1, show = 0 };
            var commandLine = new StringBuilder(string.Join(" ", values.Select(value => "\"" + value + "\"")));
            ProcessInfo child;
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x08000000,
                IntPtr.Zero, workingDirectory, ref startup, out child))
            {
                int error = Marshal.GetLastWin32Error(); CloseDesktop(desktop); throw new Win32Exception(error);
            }
            CloseHandle(child.thread);
            return new IsolatedProcess(desktop, child.process);
        }
        public bool WaitForExit(int milliseconds) => WaitForSingleObject(process, (uint)milliseconds) == 0;
        public int ExitCode { get { uint code; if (!GetExitCodeProcess(process, out code)) throw new Win32Exception(); return unchecked((int)code); } }
        public void Dispose()
        {
            if (process != IntPtr.Zero)
            {
                uint code;
                if (GetExitCodeProcess(process, out code) && code == 259) TerminateProcess(process, 1);
                CloseHandle(process); process = IntPtr.Zero;
            }
            if (desktop != IntPtr.Zero) { CloseDesktop(desktop); desktop = IntPtr.Zero; }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb; public string reserved, desktop, title;
            public uint x, y, width, height, xChars, yChars, fill, flags;
            public ushort show, reservedBytes; public IntPtr reservedPointer, input, output, error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public IntPtr process, thread; public uint processId, threadId; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    }
}
