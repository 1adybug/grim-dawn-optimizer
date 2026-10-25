using System.Collections.Concurrent;

namespace GrimDawnOptimizer.Bridge;

/// <summary>Owns all mutable view-model state, including async continuations and progress.</summary>
public sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public event Action<Exception>? UnhandledException;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        try { _queue.Add((callback, state)); }
        catch (InvalidOperationException) { /* Shutdown has drained the dispatcher. */ }
    }

    public void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            try { callback(state); }
            catch (Exception error) { UnhandledException?.Invoke(error); }
        }
    }

    public void Complete() => _queue.CompleteAdding();
    public void Dispose() => _queue.Dispose();
}
