using System.Text.Json;

namespace GrimDawnOptimizer.Bridge.PathPicking;

public sealed record PathPickerRequest(string Kind, string? DefaultPath)
{
    public bool IsFolder => Kind != "gameExecutable";
    public string Title => Kind switch
    {
        "gameDirectory" => "选择游戏安装目录",
        "saveDirectory" => "选择角色存档目录",
        _ => "选择 Grim Dawn.exe"
    };

    public static PathPickerRequest Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("kind", out var kindValue) ||
            kindValue.ValueKind != JsonValueKind.String || kindValue.GetString() is not ("gameDirectory" or "saveDirectory" or "gameExecutable"))
            throw new ArgumentException("请选择有效的路径类型：gameDirectory、saveDirectory 或 gameExecutable。");
        string? defaultPath = null;
        if (payload.TryGetProperty("defaultPath", out var pathValue) && pathValue.ValueKind != JsonValueKind.Null)
        {
            if (pathValue.ValueKind != JsonValueKind.String) throw new ArgumentException("defaultPath 必须是路径文本。");
            defaultPath = pathValue.GetString();
            if (defaultPath?.Length > 32767 || defaultPath?.Contains('\0') == true)
                throw new ArgumentException("初始路径无效。");
            // An unfinished manually typed value is only a location hint. It
            // must not prevent the user from opening the native picker.
            if (string.IsNullOrWhiteSpace(defaultPath) || !Path.IsPathFullyQualified(defaultPath)) defaultPath = null;
        }
        return new(kindValue.GetString()!, defaultPath);
    }
}

public sealed record PathPickerResult(string? Path);

public interface IPathPickerDialog
{
    string? Show(PathPickerRequest request, CancellationToken cancellationToken);
}

/// <summary>One picker at a time, on a dedicated STA; independent of both domain
/// controllers. Picking returns a path and never saves or launches anything.</summary>
public sealed class PathPickerService : IDisposable
{
    private readonly Func<IPathPickerDialog> _factory;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCancellation;
    private bool _disposed;

    public PathPickerService(Func<IPathPickerDialog>? factory = null, TimeSpan? timeout = null)
    {
        _factory = factory ?? (() => new WindowsPathPickerDialog());
        _timeout = timeout ?? TimeSpan.FromSeconds(590);
    }

    public async Task<PathPickerResult> PickAsync(JsonElement payload)
    {
        var request = PathPickerRequest.Parse(payload);
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeCancellation is not null) throw new InvalidOperationException("已有路径选择窗口打开，请先完成或取消选择。");
            cancellation = _activeCancellation = new CancellationTokenSource(_timeout);
        }
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var path = _factory().Show(request, cancellation.Token);
                completion.TrySetResult(cancellation.IsCancellationRequested ? null : path);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { completion.TrySetResult(null); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Grim Dawn Windows path picker" };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return new(await completion.Task.ConfigureAwait(false));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, cancellation)) _activeCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // The native dialog's callback posts only to its own hidden window;
            // its STA then calls IFileDialog.Close. No cross-thread COM calls.
            _activeCancellation?.Cancel();
        }
    }
}
