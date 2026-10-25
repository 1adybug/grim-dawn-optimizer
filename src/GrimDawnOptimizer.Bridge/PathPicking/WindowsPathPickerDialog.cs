using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GrimDawnOptimizer.Bridge.PathPicking;

/// <summary>Windows Common Item Dialog without a Forms/WPF dependency.</summary>
public sealed class WindowsPathPickerDialog : IPathPickerDialog
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint CancelMessage = 0x8001, TimerMessage = 0x113;
    private const uint ForceFileSystem = 0x40, PickFolders = 0x20, PathMustExist = 0x800,
        FileMustExist = 0x1000, NoChangeDirectory = 0x8, AllowMultiSelect = 0x200;
    private readonly Func<IFileOpenDialog, IntPtr, int> _show;

    public WindowsPathPickerDialog() : this((dialog, owner) => dialog.Show(owner)) { }

    // Tests use the real COM object and configuration but replace only Show,
    // verifying the interop vtable without opening a user-visible window.
    internal WindowsPathPickerDialog(Func<IFileOpenDialog, IntPtr, int> show) => _show = show;

    public string? Show(PathPickerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Set awareness before COM can create shell windows. Keep it through
        // the modal pump and all cleanup; HWNDs inherit their creator's context.
        using var dpiAwareness = ThreadDpiAwarenessScope.EnterPerMonitorV2();
        var initialized = CoInitializeEx(IntPtr.Zero, 0x6); // STA + disable obsolete OLE1 DDE.
        Marshal.ThrowExceptionForHR(initialized);
        IFileOpenDialog? dialog = null;
        IntPtr messageWindow = IntPtr.Zero;
        var className = "GrimDawnPathPicker." + Guid.NewGuid().ToString("N");
        var instance = GetModuleHandle(null);
        ushort registered = 0;
        WindowProcedure? procedure = null;
        try
        {
            var owner = GetForegroundWindow();
            if (!IsWindow(owner)) owner = IntPtr.Zero;
            dialog = (IFileOpenDialog)new FileOpenDialogCom();
            dialog.GetOptions(out var options);
            options = (options | ForceFileSystem | PathMustExist | NoChangeDirectory) & ~AllowMultiSelect;
            dialog.SetOptions(request.IsFolder ? options | PickFolders : options | FileMustExist);
            dialog.SetTitle(request.Title);
            dialog.SetOkButtonLabel(request.IsFolder ? "选择此文件夹" : "选择此文件");
            if (!request.IsFolder)
            {
                dialog.SetFileTypes(2,
                [new("Grim Dawn.exe", "Grim Dawn.exe"), new("可执行程序 (*.exe)", "*.exe")]);
                dialog.SetFileTypeIndex(1);
                dialog.SetFileName("Grim Dawn.exe");
            }
            SetInitialFolder(dialog, request.DefaultPath);

            var activated = false;
            procedure = (window, message, wparam, lparam) =>
            {
                if (message is CancelMessage or TimerMessage)
                {
                    // Show runs a modal message pump on this same STA. Retry a
                    // cancellation that arrived while the dialog was initializing.
                    if (cancellationToken.IsCancellationRequested) dialog.Close(Cancelled);
                    else if (!activated)
                    {
                        try
                        {
                            if (((IOleWindow)dialog).GetWindow(out var handle) >= 0 && handle != IntPtr.Zero)
                            {
                                SetForegroundWindow(handle);
                                activated = true;
                            }
                        }
                        catch (COMException) { /* The dialog has not created its HWND yet. */ }
                    }
                    return IntPtr.Zero;
                }
                return DefWindowProc(window, message, wparam, lparam);
            };
            var windowClass = new WindowClass
            {
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance,
                ClassName = className
            };
            registered = RegisterClass(ref windowClass);
            if (registered == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            messageWindow = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, instance, IntPtr.Zero);
            if (messageWindow == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetTimer(messageWindow, new UIntPtr(1), 100, IntPtr.Zero) == UIntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using var registration = cancellationToken.Register(() => PostMessage(messageWindow, CancelMessage, UIntPtr.Zero, IntPtr.Zero));
            cancellationToken.ThrowIfCancellationRequested();
            var result = _show(dialog, owner);
            if (result == Cancelled || cancellationToken.IsCancellationRequested) return null;
            Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(0x80058000, out var path); // SIGDN_FILESYSPATH
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            finally { Marshal.FinalReleaseComObject(item); }
        }
        finally
        {
            if (messageWindow != IntPtr.Zero)
            {
                KillTimer(messageWindow, new UIntPtr(1));
                DestroyWindow(messageWindow);
            }
            if (registered != 0) UnregisterClass(className, instance);
            if (dialog is not null) Marshal.FinalReleaseComObject(dialog);
            GC.KeepAlive(procedure);
            CoUninitialize();
        }
    }

    private static void SetInitialFolder(IFileOpenDialog dialog, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        // A stale or unfinished manual path is a hint, not an error. Start at
        // its nearest existing folder and let the user navigate normally.
        string? folder = path;
        while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            var parent = Path.GetDirectoryName(folder);
            if (parent == folder) break;
            folder = parent;
        }
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        var shellItemId = typeof(IShellItem).GUID;
        var result = SHCreateItemFromParsingName(folder, IntPtr.Zero, ref shellItemId, out var item);
        if (result < 0) return;
        try { dialog.SetFolder(item); }
        finally { Marshal.FinalReleaseComObject(item); }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialogCom { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FilterSpec(string name, string specification)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Specification = specification;
    }

    // Full inherited vtable order: IModalWindow, IFileDialog, IFileOpenDialog.
    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr value);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint format, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig] int GetWindow(out IntPtr window);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr window, UIntPtr id);
}
