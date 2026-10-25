using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GrimDawnOptimizer.Bridge.PathPicking;

/// <summary>Sets only the current picker thread's DPI context. The console
/// sidecar and test/dotnet hosts may otherwise default to DPI-unaware.</summary>
internal sealed class ThreadDpiAwarenessScope : IDisposable
{
    private static readonly IntPtr PerMonitorV2 = new(-4);
    private IntPtr _previous;

    private ThreadDpiAwarenessScope(IntPtr previous) => _previous = previous;

    public static ThreadDpiAwarenessScope EnterPerMonitorV2()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorV2);
        if (previous == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用文件选择窗口的高 DPI 显示。");
        return new(previous);
    }

    public void Dispose()
    {
        if (_previous == IntPtr.Zero) return;
        // The caller disposes this after releasing every dialog/window and COM
        // object, on the same synchronous STA that entered the scope.
        if (SetThreadDpiAwarenessContext(_previous) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法还原文件选择线程的 DPI 状态。");
        _previous = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
