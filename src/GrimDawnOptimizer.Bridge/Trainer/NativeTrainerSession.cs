using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GrimDawnOptimizer.Bridge.Trainer;

/// <summary>Versioned native session. Connecting opens game memory;
/// process discovery and construction do not load the trainer.</summary>
public sealed class NativeTrainerSession : ITrainerSession
{
    public const int Capacity = 4096, StateSize = 1136;
    public const uint Magic = 0x43444731, ProtocolVersion = 7;
    public static readonly string[] SupportedHashes =
    [
        "580ad41cc1c3e4cab6d29e71cb60d3a638079582eb3b5da6eec5ffea54627b7d",
        "cd7ecd7942d719f96b42aff4896f8c25d52152b4a41191f71a4a66ea48cecad2"
    ];
    private readonly object _gate = new();
    private readonly string _nativeDirectory;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private Process? _process, _injector;
    private FileStream? _connectionLock;
    private Timer? _heartbeat;
    private bool _disposed;
    private int _requested, _movementMultiplier = 2;
    private string _lifeMode = "infinite";
    public int Requested { get { lock (_gate) return _requested; } }
    public int ProcessId { get { lock (_gate) return _process?.Id ?? 0; } }
    public string Architecture { get; private set; } = "";
    public bool Connected
    {
        get
        {
            lock (_gate)
            {
                try { return !_disposed && _view is not null && _process is not null && !_process.HasExited; }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return false; }
            }
        }
    }

    public NativeTrainerSession(string? nativeDirectory = null) => _nativeDirectory =
        nativeDirectory ?? Environment.GetEnvironmentVariable("GRIM_DAWN_TRAINER_NATIVE_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "trainer-native");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort machine, out ushort native);

    public static int? FindRunningProcess(string executable)
    {
        var games = Process.GetProcessesByName("Grim Dawn");
        try
        {
            var matches = games.Where(process => !process.HasExited &&
                string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0].Id : null;
        }
        finally { foreach (var game in games) game.Dispose(); }
    }

    public async Task ConnectAsync(string executable, CancellationToken cancellationToken)
    {
        var selected = TrainerSettings.Validate(executable);
        var games = Process.GetProcessesByName("Grim Dawn");
        Process? target = null;
        try
        {
            var matching = games.Where(p => !p.HasExited &&
                string.Equals(p.MainModule?.FileName, selected, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0)
                throw new InvalidOperationException(games.Length == 0 ? "游戏尚未运行，请先启动游戏。" : "运行中的游戏与所选路径不同，请重新选择游戏程序。");
            if (matching.Length != 1) throw new InvalidOperationException("检测到多个对应游戏进程，请保留一个后连接。");
            target = matching[0];
            lock (_gate)
            {
                ThrowIfClosed(cancellationToken);
                _process = target;
            }
            if (!IsWow64Process2(target.Handle, out var machine, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Architecture = machine == 0x14c ? "x86" : machine == 0 ? "x64" : throw new InvalidOperationException("不支持这个处理器架构。");
            var loadedBridges = target.Modules.Cast<ProcessModule>()
                .Where(m => m.ModuleName.StartsWith("CairnBridge.", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (loadedBridges.Any(m =>
                !m.ModuleName.Equals($"CairnBridge.v{ProtocolVersion}.{Architecture}.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("游戏中仍有旧版组件，请重启游戏后再连接新版工具。");
            string hash;
            await using (var file = File.OpenRead(Path.Combine(Path.GetDirectoryName(selected)!, "Game.dll")))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            if (!SupportedHashes.Contains(hash))
                throw new InvalidOperationException("此 Game.dll 与已适配的 v1.3.1.1 不匹配，已停止连接。更新游戏后需要重新适配。");
            var helper = Path.GetFullPath(Path.Combine(_nativeDirectory, $"CairnInject.{Architecture}.exe"));
            var dll = Path.GetFullPath(Path.Combine(_nativeDirectory, $"CairnBridge.v{ProtocolVersion}.{Architecture}.dll"));
            if (!File.Exists(helper) || !File.Exists(dll))
                throw new FileNotFoundException("实战组件缺失，请保留程序目录内完整的 trainer-native 文件夹。");
            // The protocol filename can stay the same across native bug fixes.
            // The injector reuses an already-loaded module, so check its bytes too.
            foreach (var loaded in loadedBridges)
                TrainerBridgeCache.RequireMatchingLoadedBridge(loaded.FileName, dll);
            dll = await TrainerBridgeCache.PrepareAsync(dll, cancellationToken);

            Process injection;
            lock (_gate)
            {
                ThrowIfClosed(cancellationToken);
                _connectionLock = new FileStream(Path.Combine(Path.GetTempPath(), $"CairnCompanion.{target.Id}.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _mapping = MemoryMappedFile.CreateOrOpen($"Local\\GrimDawnCompanion.v{ProtocolVersion}.{target.Id}", Capacity, MemoryMappedFileAccess.ReadWrite);
                _view = _mapping.CreateViewAccessor(0, Capacity);
                if (_view.ReadUInt32(0) != Magic)
                {
                    _view.WriteArray(0, new byte[Capacity], 0, Capacity);
                    _view.Write(0, Magic);
                    _view.Write(4, ProtocolVersion);
                }
                else if (_view.ReadUInt32(4) != ProtocolVersion)
                    throw new InvalidOperationException("连接协议不匹配，请重启游戏后重试。");
                _requested = 0;
                _view.Write(8, 0);
                _view.Write(36, _movementMultiplier);
                _view.Write(40, _lifeMode == "lock" ? 1 : 0);
                _view.Write(736, _view.ReadUInt32(732));
                _view.Write(1128, 0);
                _view.Write(16, Environment.TickCount64);
                // Independent of browser scheduling, active tab and optimizer work.
                _heartbeat = new Timer(_ => Heartbeat(), null, 250, 250);
                var start = new ProcessStartInfo(helper)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                start.ArgumentList.Add(target.Id.ToString());
                start.ArgumentList.Add(dll);
                injection = _injector = Process.Start(start) ?? throw new InvalidOperationException("无法启动连接组件。");
            }
            var stdout = injection.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = injection.StandardError.ReadToEndAsync(cancellationToken);
            await injection.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            var output = await stdout;
            var error = await stderr;
            if (injection.ExitCode != 0) throw new InvalidOperationException($"连接失败（{injection.ExitCode}）：{output} {error}");
            for (var attempt = 0; attempt < 60; attempt++)
            {
                lock (_gate)
                {
                    ThrowIfClosed(cancellationToken);
                    var status = _view!.ReadInt32(24);
                    if (status < 0) throw new InvalidOperationException(ReadMessage(_view));
                    if (status > 0) return;
                }
                await Task.Delay(100, cancellationToken);
            }
            throw new TimeoutException("游戏未确认连接，所有效果保持关闭。请重启游戏后重试。");
        }
        catch { Dispose(); throw; }
        finally
        {
            foreach (var game in games) if (!ReferenceEquals(game, _process)) game.Dispose();
        }
    }

    private void ThrowIfClosed(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void Heartbeat()
    {
        lock (_gate)
        {
            if (_disposed || _view is null) return;
            try { _view.Write(16, Environment.TickCount64); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }

    public void SetFlags(int flags)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _requested = flags & TrainerFeatures.Mask;
            _view?.Write(8, _requested);
        }
    }

    public void SetMovementMultiplier(int value)
    {
        if (!TrainerFeatures.MovementMultipliers.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_gate)
        {
            if (_disposed) return;
            _movementMultiplier = value;
            _view?.Write(36, value);
        }
    }

    public void SetLifeMode(string value)
    {
        if (!TrainerFeatures.LifeModes.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_gate)
        {
            if (_disposed) return;
            _lifeMode = value;
            _view?.Write(40, value == "lock" ? 1 : 0);
        }
    }

    public void ToggleOverlay()
    {
        lock (_gate)
        {
            if (_disposed || _view is null) return;
            _view.Write(720, unchecked(_view.ReadInt32(720) + 1));
        }
    }

    public IReadOnlyList<TrainerOverlayAction> ReadOverlayActions()
    {
        lock (_gate)
        {
            if (_disposed || _view is null) return [];
            var read = _view.ReadUInt32(736);
            var write = _view.ReadUInt32(732);
            Thread.MemoryBarrier();
            if (_view.ReadInt32(1128) != 0)
            {
                _view.Write(736, write); _view.Write(1128, 0);
                return [new(4, 0, 0)];
            }
            var count = unchecked(write - read);
            if (count > 32) throw new InvalidDataException("游戏弹窗操作队列无效，请重新连接。");
            var actions = new List<TrainerOverlayAction>();
            for (uint index = 0; index < count; ++index)
            {
                var offset = 744 + ((read + index) % 32) * 12;
                actions.Add(new(_view.ReadInt32(offset), _view.ReadInt32(offset + 4), _view.ReadInt32(offset + 8)));
            }
            Thread.MemoryBarrier();
            _view.Write(736, write);
            return actions;
        }
    }

    public TrainerRuntimeSnapshot? Read()
    {
        lock (_gate)
        {
            if (!Connected || _view is null) return null;
            var bytes = new byte[StateSize];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var before = _view.ReadInt32(32);
                if ((before & 1) != 0) continue;
                _view.ReadArray(0, bytes, 0, bytes.Length);
                if (before == _view.ReadInt32(32)) return Decode(bytes);
            }
            return null;
        }
    }

    public static TrainerRuntimeSnapshot Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < StateSize || BitConverter.ToUInt32(data) != Magic || BitConverter.ToUInt32(data[4..]) != ProtocolVersion)
            throw new InvalidDataException("实战组件快照协议不匹配。");
        return new(BitConverter.ToInt32(data[24..]), BitConverter.ToInt32(data[12..]) & TrainerFeatures.Mask,
            BitConverter.ToUInt32(data[28..]), BitConverter.ToUInt64(data[48..]),
            BitConverter.ToDouble(data[56..]), BitConverter.ToSingle(data[64..]), BitConverter.ToSingle(data[68..]), BitConverter.ToSingle(data[72..]),
            BitConverter.ToSingle(data[704..]), BitConverter.ToSingle(data[708..]), BitConverter.ToInt32(data[712..]), BitConverter.ToInt32(data[716..]),
            Encoding.Unicode.GetString(data.Slice(120, 512)).Split('\0')[0], BitConverter.ToUInt32(data[44..]), BitConverter.ToInt32(data[724..]),
            BitConverter.ToUInt32(data[1132..]));
    }

    private static string ReadMessage(MemoryMappedViewAccessor view)
    {
        var bytes = new byte[512];
        view.ReadArray(120, bytes, 0, bytes.Length);
        return Encoding.Unicode.GetString(bytes).Split('\0')[0];
    }

    public static void LaunchGame(string executable)
    {
        var info = TrainerSettings.LaunchInfo(executable);
        var games = Process.GetProcessesByName("Grim Dawn");
        try
        {
            if (games.Any(p => !p.HasExited)) throw new InvalidOperationException("游戏已经运行，请点击连接游戏。");
        }
        finally { foreach (var game in games) game.Dispose(); }
        using var launched = Process.Start(info) ?? throw new InvalidOperationException("游戏未能启动。");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _requested = 0;
            _heartbeat?.Dispose();
            _heartbeat = null;
            if (_view is not null)
            {
                try { _view.Write(8, 0); _view.Write(16, 0L); }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            }
            _view?.Dispose(); _view = null;
            _mapping?.Dispose(); _mapping = null;
            // Stop only our helper when a pending connect is cancelled, never the game.
            if (_injector is not null)
            {
                try { if (!_injector.HasExited) _injector.Kill(entireProcessTree: false); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                _injector.Dispose(); _injector = null;
            }
            _process?.Dispose(); _process = null;
            _connectionLock?.Dispose(); _connectionLock = null;
        }
    }
}
