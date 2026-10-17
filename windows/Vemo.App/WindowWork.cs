namespace Vemo.App;

// All work that may use the store is registered before it starts. Closing stops
// registration, requests cancellation, then drains even non-cancellable work.
public sealed class WindowWork : IDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource lifetime = new();
    readonly HashSet<Task> pending = [];
    bool stopping;
    public CancellationToken Token => lifetime.Token;
    public Task Start(Func<CancellationToken, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (stopping) return Task.CompletedTask;
            pending.Add(completion.Task);
        }
        _ = Run(action, completion);
        return completion.Task;
    }
    async Task Run(Func<CancellationToken, Task> action, TaskCompletionSource completion)
    {
        try { await action(lifetime.Token); completion.TrySetResult(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { completion.TrySetCanceled(lifetime.Token); }
        catch (Exception e) { completion.TrySetException(e); }
        finally { lock (gate) pending.Remove(completion.Task); }
    }
    public async Task StopAsync()
    {
        Task[] snapshot;
        lock (gate) { stopping = true; snapshot = pending.ToArray(); }
        lifetime.Cancel();
        try { await Task.WhenAll(snapshot); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public void Dispose() => lifetime.Dispose();
}
