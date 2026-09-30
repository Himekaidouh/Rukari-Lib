using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Rukari.CharacterVoice.Runtime;

internal interface IVoiceFolderPicker
{
    Task<string?> PickAsync(CancellationToken cancellationToken);
}

/// <summary>Common Item Dialog on a dedicated STA; no shell, helper process or runtime compilation.</summary>
internal sealed class WindowsVoiceFolderPicker : IVoiceFolderPicker
{
    private readonly string _title;
    private readonly Action<string> _log;
    private int _active;
    private const int Cancelled = unchecked((int)0x800704C7);
    internal WindowsVoiceFolderPicker() : this("选择音频文件夹", _ => { }) { }
    internal WindowsVoiceFolderPicker(string title, Action<string> log) { _title = title; _log = log; }

    public Task<string?> PickAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("系统文件夹选择器仅支持 Windows。");
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            throw new IOException("上一个文件夹窗口尚在关闭，请稍候再试。");
        try
        {
            IntPtr owner = GetForegroundWindow();
            GetWindowThreadProcessId(owner, out uint pid);
            if (pid != Environment.ProcessId) { using var process = Process.GetCurrentProcess(); owner = process.MainWindowHandle; }
            return RunOnStaAsync(() => Show(owner, ct), ct, () => Volatile.Write(ref _active, 0));
        }
        catch { Volatile.Write(ref _active, 0); throw; }
    }

    // Cancelling releases the page immediately. The STA owns cleanup, including a dialog
    // that finishes opening late; no Thread.Abort, Unity-thread wait, or cross-thread COM call.
    internal static Task<string?> RunOnStaAsync(Func<string?> operation, CancellationToken ct, Action? finished = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("系统文件夹选择器仅支持 Windows。");
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ct.ThrowIfCancellationRequested(); string? result = operation(); ct.ThrowIfCancellationRequested(); completion.TrySetResult(result); }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { finished?.Invoke(); }
        }) { IsBackground = true, Name = "Rukari audio folder dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        _ = completion.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task.WaitAsync(ct);
    }

    private string? Show(IntPtr owner, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var clock = Stopwatch.StartNew();
        Check(CoInitializeEx(IntPtr.Zero, 2)); // COINIT_APARTMENTTHREADED, balanced even for S_FALSE.
        IFileDialog? dialog = null; uint cookie = 0; bool advised = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            GetWindowThreadProcessId(owner, out uint pid);
            if (owner == IntPtr.Zero || pid != Environment.ProcessId) throw new IOException("游戏窗口尚未就绪，请回到游戏后重试。");
            dialog = CreateDialog(ct, () => _log($"folder-picker native window ready after {clock.ElapsedMilliseconds}ms"), out var events, out cookie);
            advised = true;
            using var cancel = ct.Register(events.RequestClose);
            ct.ThrowIfCancellationRequested();
            _log($"folder-picker native show after {clock.ElapsedMilliseconds}ms; owner={owner.ToInt64():X}");
            int result;
            try { result = dialog.Show(owner); }
            finally { events.ForgetWindow(); }
            ct.ThrowIfCancellationRequested();
            if (result == Cancelled) return null;
            Check(result); Check(dialog.GetResult(out IShellItem item));
            try
            {
                Check(item.GetDisplayName(0x80058000, out IntPtr path)); // SIGDN_FILESYSPATH
                try
                {
                    string value = Marshal.PtrToStringUni(path) ?? throw new IOException("没有取得文件夹路径。");
                    if (!Path.IsPathFullyQualified(value) || !Directory.Exists(value)) throw new IOException("所选文件夹不可用。");
                    _log($"folder-picker native selected after {clock.ElapsedMilliseconds}ms");
                    return value;
                }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        finally
        {
            try
            {
                if (dialog is not null)
                {
                    try { if (advised) dialog.Unadvise(cookie); }
                    finally { Marshal.ReleaseComObject(dialog); }
                }
            }
            finally { CoUninitialize(); }
        }
    }
    private IFileDialog CreateDialog(CancellationToken ct, Action ready, out DialogEvents events, out uint cookie)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Guid clsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"), iid = typeof(IFileDialog).GUID;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var dialog));
        try
        {
            Check(dialog.GetOptions(out uint options));
            Check(dialog.SetOptions(options | 0x20 | 0x40 | 0x800 | 0x8 | 0x02000000));
            Check(dialog.SetTitle(_title)); Check(dialog.SetOkButtonLabel("选择此文件夹"));
            events = new DialogEvents(ct, ready);
            Check(dialog.Advise(events, out cookie));
            return dialog;
        }
        catch { Marshal.ReleaseComObject(dialog); throw; }
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DialogEvents : IFileDialogEvents
    {
        private readonly CancellationToken _cancel;
        private readonly Action _ready;
        private long _window;
        internal DialogEvents(CancellationToken cancel, Action ready) { _cancel = cancel; _ready = ready; }
        internal void ForgetWindow() => Interlocked.Exchange(ref _window, 0);
        internal void RequestClose()
        {
            IntPtr window = new(Interlocked.Read(ref _window));
            if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); // Only this dialog's WM_CLOSE.
        }
        public int OnFolderChange(IFileDialog dialog)
        {
            try
            {
                Check(((IOleWindow)dialog).GetWindow(out IntPtr window));
                if (Interlocked.Exchange(ref _window, window.ToInt64()) == 0) _ready();
                if (_cancel.IsCancellationRequested) dialog.Close(Cancelled); // On the owning STA.
            }
            catch { if (_cancel.IsCancellationRequested) dialog.Close(Cancelled); }
            return 0;
        }
        public int OnFileOk(IFileDialog dialog) => 0;
        public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => 0;
        public int OnSelectionChange(IFileDialog dialog) => 0;
        public int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response) { response = 0; return 0; }
        public int OnTypeChange(IFileDialog dialog) => 0;
        public int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response) { response = 0; return 0; }
    }

    // Windows SDK IModalWindow + IFileDialog vtable order; retain unused slots.
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        [PreserveSig] int SetFileTypes(uint count, IntPtr filters);
        [PreserveSig] int SetFileTypeIndex(uint index);
        [PreserveSig] int GetFileTypeIndex(out uint index);
        [PreserveSig] int Advise(IFileDialogEvents events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int SetDefaultFolder(IShellItem item);
        [PreserveSig] int SetFolder(IShellItem item);
        [PreserveSig] int GetFolder(out IShellItem item);
        [PreserveSig] int GetCurrentSelection(out IShellItem item);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetFileName(out IntPtr name);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int GetResult(out IShellItem item);
        [PreserveSig] int AddPlace(IShellItem item, uint placement);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int result);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr filter);
    }
    [ComVisible(true), Guid("973510db-7d7f-452b-8975-74a85828d354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig] int OnFileOk(IFileDialog dialog);
        [PreserveSig] int OnFolderChanging(IFileDialog dialog, IShellItem folder);
        [PreserveSig] int OnFolderChange(IFileDialog dialog);
        [PreserveSig] int OnSelectionChange(IFileDialog dialog);
        [PreserveSig] int OnShareViolation(IFileDialog dialog, IShellItem item, out uint response);
        [PreserveSig] int OnTypeChange(IFileDialog dialog);
        [PreserveSig] int OnOverwrite(IFileDialog dialog, IShellItem item, out uint response);
    }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint name, out IntPtr result);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem item, uint hint, out int order);
    }
    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IFileDialog dialog);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
