namespace VNotch.ApplicationServices;

/// <summary>At most one running query and one replacement; superseded callers observe cancellation.</summary>
public sealed class LatestQuery<T>
{
    private sealed record Work(Func<CancellationToken, T> Query, CancellationTokenSource Cancellation, TaskCompletionSource<T> Completion);
    private readonly object _gate = new();
    private Work? _next;
    private Work? _active;
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _closed;

    public Task<T> Submit(Func<CancellationToken, T> query, CancellationToken cancellation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _active?.Cancellation.Cancel();
            if (_next is { } superseded)
            {
                superseded.Completion.TrySetCanceled();
                superseded.Cancellation.Dispose();
            }
            var work = new Work(query, CancellationTokenSource.CreateLinkedTokenSource(cancellation),
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            _next = work;
            if (!_running) { _running = true; _worker = Task.Run(Drain); }
            return work.Completion.Task;
        }
    }

    private void Drain()
    {
        while (true)
        {
            Work work;
            lock (_gate)
            {
                if (_next == null) { _running = false; return; }
                work = _next; _next = null; _active = work;
            }
            try
            {
                work.Cancellation.Token.ThrowIfCancellationRequested();
                T result = work.Query(work.Cancellation.Token);
                work.Cancellation.Token.ThrowIfCancellationRequested();
                work.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { work.Completion.TrySetCanceled(); }
            catch (Exception ex) { work.Completion.TrySetException(ex); }
            finally { lock (_gate) { _active = null; work.Cancellation.Dispose(); } }
        }
    }

    public Task CompleteAsync()
    {
        lock (_gate)
        {
            _closed = true;
            _active?.Cancellation.Cancel();
            _next?.Cancellation.Cancel();
            return _worker;
        }
    }
}
