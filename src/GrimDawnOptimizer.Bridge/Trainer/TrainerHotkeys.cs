using System.Runtime.InteropServices;

namespace GrimDawnOptimizer.Bridge.Trainer;

/// <summary>Registers shortcuts only while connected. Its Windows message loop
/// is independent of the webview and does not require Windows Forms.</summary>
public sealed class TrainerHotkeys : ITrainerHotkeys
{
    private Thread? _thread;
    private uint _threadId;
    private bool _disposed;

    public string? Start(Action<int> onHotkey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        var started = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            var registered = new List<int>();
            try
            {
                _threadId = GetCurrentThreadId();
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create the thread message queue.
                var failed = new List<string>();
                foreach (var id in Enumerable.Range(1, 9).Append(12))
                {
                    if (RegisterHotKey(IntPtr.Zero, id, 0x4003, (uint)(0x70 + id - 1))) registered.Add(id);
                    else failed.Add($"Ctrl+Alt+F{id}");
                }
                started.TrySetResult(failed.Count == 0 ? null : $"快捷键被占用：{string.Join("、", failed)}。仍可使用界面开关。");
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                    if (message.Message == 0x312) onHotkey(unchecked((int)message.WParam.ToUInt64()));
            }
            catch (Exception error) { started.TrySetResult($"快捷键未能启用：{error.Message}。仍可使用界面开关。"); }
            finally
            {
                foreach (var id in registered) UnregisterHotKey(IntPtr.Zero, id);
                _threadId = 0;
            }
        }) { IsBackground = true, Name = "Grim Dawn trainer shortcuts" };
        _thread.Start();
        try { return started.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { Stop(); return "快捷键初始化超时，仍可使用界面开关。"; }
    }

    public void Stop()
    {
        var thread = _thread;
        if (thread is null) return;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x12, UIntPtr.Zero, IntPtr.Zero);
        if (thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() { Stop(); _disposed = true; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wparam, IntPtr lparam);
}
