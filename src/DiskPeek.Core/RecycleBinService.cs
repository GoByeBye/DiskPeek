using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskPeek.Core;

public sealed record RecycleResult(bool Success, bool Cancelled, string Message);

// Deletion belongs to the unprivileged app, never to the elevated scan worker.
// There is deliberately no permanent-delete implementation or fallback here.
public static class RecycleBinService
{
    private const FileAttributes UnsupportedAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;
    public static RecycleResult ValidateSelection(ScanResult result, int index)
    {
        try { GetSelection(result, index); return new(true, false, "Move this item to the Recycle Bin."); }
        catch (Exception ex) when (Expected(ex)) { return new(false, false, ex.Message); }
    }

    public static Task<RecycleResult> MoveAsync(ScanResult result, int index, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(new RecycleResult(false, true, "Recycling cancelled."));
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Move(result, index, cancellationToken)); }
            catch (OperationCanceledException) { completion.TrySetResult(new(false, true, "Recycling cancelled.")); }
            catch (Exception ex) { completion.TrySetResult(new(false, false, "Could not recycle the item. " + ex.Message)); }
        }) { IsBackground = true, Name = "DiskPeek Recycle Bin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static RecycleResult Move(ScanResult result, int index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = GetSelection(result, index);
        var held = new List<SafeFileHandle>();
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            HoldAndValidateAncestors(result, index, path, held);
            ValidateItem(path, result.Nodes[index], held);
            token.ThrowIfCancellationRequested();
            operation = (IFileOperation)(Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!);
            // RECYCLEONDELETE requests recycling, not the best-effort legacy ALLOWUNDO.
            // NOERRORUI + EARLYFAILURE stop on errors; no elevation prompt is enabled.
            const uint flags = 0x00080000 | 0x20000000 | 0x00100000 | 0x00000400 | 0x00000010 | 0x00000004 | 0x00002000;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
            Guid iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            var sink = new RecycleSink(token, () => ValidateItem(path, result.Nodes[index], null));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
            int performed = operation.PerformOperations();
            int abortedResult = operation.GetAnyOperationsAborted(out bool aborted);
            GC.KeepAlive(sink);
            if (sink.Recycled && performed >= 0 && abortedResult >= 0 && !aborted)
                return new(true, false, "Moved to the Recycle Bin. Rescan to refresh size totals.");
            if (token.IsCancellationRequested && !sink.Recycled) return new(false, true, "Recycling cancelled.");
            if (sink.Refusal != null) return new(false, false, sink.Refusal);
            int error = sink.ItemResult < 0 ? sink.ItemResult : performed < 0 ? performed : abortedResult;
            string detail = error < 0 ? Marshal.GetExceptionForHR(error)?.Message ?? "Windows rejected the operation." : "Windows did not complete recycling.";
            return new(false, false, detail + " The app will not permanently delete the item instead.");
        }
        catch (OperationCanceledException) { return new(false, true, "Recycling cancelled."); }
        catch (Exception ex) when (Expected(ex)) { return new(false, false, ex.Message); }
        finally
        {
            if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            if (operation != null && Marshal.IsComObject(operation)) Marshal.FinalReleaseComObject(operation);
            for (int i = held.Count - 1; i >= 0; i--) held[i].Dispose();
        }
    }

    private static string GetSelection(ScanResult result, int index)
    {
        if ((uint)index >= (uint)result.Count || index == 0) throw new InvalidOperationException("Select a file or folder inside the scan. The scan root cannot be recycled here.");
        var node = result.Nodes[index];
        if (!node.Identity.IsKnown) throw new InvalidOperationException("This scan has no reliable identity for the selected item. Rescan before recycling it.");
        if ((node.Attributes & UnsupportedAttributes) != 0 || (node.Flags & NodeFlags.ReparseSkipped) != 0)
            throw new InvalidOperationException("Links, mounted folders, and cloud placeholders cannot be recycled here. Use Explorer for those items.");
        for (int current = index, steps = 0; current > 0; current = result.Nodes[current].ParentIndex)
        {
            if ((uint)current >= (uint)result.Count || steps++ >= result.Count) throw new InvalidDataException("The scan path is invalid. Rescan before recycling.");
            string name = result.Nodes[current].Name;
            if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("The scan path is invalid. Rescan before recycling.");
        }
        string scope = Normalize(result.Target), path = Normalize(result.GetPath(index));
        string relative = Path.GetRelativePath(scope, path);
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith("..\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Only items inside the scanned folder can be recycled.");
        string? root = Path.GetPathRoot(path);
        if (root == null || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new InvalidOperationException("Recycling here is limited to fixed local drives. Network and removable locations must be managed in Explorer.");
        foreach (string component in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (component.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || component.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Windows storage-management folders cannot be recycled here.");
        return path;
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("The scan path is not fully qualified.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void HoldAndValidateAncestors(ScanResult result, int index, string path, List<SafeFileHandle> held)
    {
        string root = Path.GetPathRoot(path)!;
        string[] components = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string scope = Normalize(result.Target);
        int scopeDepth = scope.Length <= root.Length ? 0 : scope[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
        var ancestors = new List<int>();
        for (int parent = result.Nodes[index].ParentIndex, steps = 0; parent >= 0 && steps++ < result.Count; parent = result.Nodes[parent].ParentIndex) ancestors.Add(parent);
        ancestors.Reverse();
        string current = root;
        for (int depth = 0; depth < components.Length; depth++)
        {
            var handle = OpenMetadata(current, share: 3); // Hold each ancestor against rename/replacement while Shell acts.
            held.Add(handle);
            var live = ReadMetadata(handle);
            if ((live.Attributes & UnsupportedAttributes) != 0)
                throw new InvalidOperationException("The path now passes through a link, mount point, or offline folder. Rescan or use Explorer.");
            int relativeDepth = depth - scopeDepth;
            if (relativeDepth >= 0 && relativeDepth < ancestors.Count)
            {
                var expected = result.Nodes[ancestors[relativeDepth]].Identity;
                if (expected.IsKnown && expected != live.Identity) throw new InvalidOperationException("A parent folder changed since the scan. Rescan before recycling.");
            }
            current = Path.Combine(current, components[depth]);
        }
    }

    private static void ValidateItem(string path, ScanNode expected, List<SafeFileHandle>? held)
    {
        var handle = OpenMetadata(path, share: 7);
        try
        {
            var live = ReadMetadata(handle);
            if ((live.Attributes & UnsupportedAttributes) != 0 ||
                ((live.Attributes & FileAttributes.Directory) != 0) != expected.IsDirectory || live.Identity != expected.Identity)
                throw new InvalidOperationException("The item changed since the scan. Rescan before recycling it.");
            if (held != null) { held.Add(handle); handle = null!; }
        }
        finally { handle?.Dispose(); }
    }

    private static SafeFileHandle OpenMetadata(string path, uint share)
    {
        var handle = CreateFileW(NativeDirectory.ExtendedPath(path), 0x80, share, IntPtr.Zero, 3, 0x02000000 | 0x00200000 | 0x00100000, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }
    private static (FileAttributes Attributes, FileIdentity Identity) ReadMetadata(SafeFileHandle handle)
    {
        if (!GetAttributeTagInfo(handle, 9, out var attributes, 8) || !GetIdentityInfo(handle, 18, out var identity, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return ((FileAttributes)attributes.Attributes, new(identity.Low, identity.High));
    }
    private static bool Expected(Exception ex) => ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or Win32Exception or COMException or NotSupportedException;
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { public uint Attributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] private struct IdentityInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] private static extern bool GetAttributeTagInfo(SafeFileHandle handle, int informationClass, out AttributeInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] private static extern bool GetIdentityInfo(SafeFileHandle handle, int informationClass, out IdentityInfo info, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr changes);
        [PreserveSig] int SetOwnerWindow(IntPtr window);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(CancellationToken token, Action validate) : IFileOperationProgressSink
    {
        public bool Recycled { get; private set; }
        public int ItemResult { get; private set; }
        public string? Refusal { get; private set; }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            if (token.IsCancellationRequested) return unchecked((int)0x80004004);
            if ((flags & 0x80) == 0) { Refusal = "Windows cannot recycle this item. Permanent deletion was refused."; return unchecked((int)0x80004004); }
            try { validate(); return 0; }
            catch (Exception ex) { Refusal = ex.Message; return unchecked((int)0x80004004); }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? created) { ItemResult = result; Recycled |= result >= 0 && created != null; return 0; }
        public int StartOperations() => token.IsCancellationRequested ? unchecked((int)0x80004004) : 0;
        public int FinishOperations(int result) { if (result < 0) ItemResult = result; return 0; }
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? created) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string name, int result, IShellItem? created) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string template, uint attributes, int result, IShellItem? created) => 0;
        public int UpdateProgress(uint total, uint completed) => token.IsCancellationRequested ? unchecked((int)0x80004004) : 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
