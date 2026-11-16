namespace VNotch.ApplicationServices;

/// <summary>Bounds retained work while preserving accepted writes and their submission order.</summary>
public sealed class BoundedSerialQueue(int capacity = 64)
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private bool _closed;
    public Task<T> Enqueue<T>(Func<T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_queue.Count >= capacity) return Task.FromException<T>(new InvalidOperationException("Clipboard work queue is full; retry the operation."));
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(() =>
            {
                try { completion.SetResult(action()); }
                catch (OperationCanceledException ex) { completion.SetCanceled(ex.CancellationToken); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            if (!_running)
            {
                _running = true;
                _worker = Task.Run(() =>
                {
                    while (true)
                    {
                        Action next;
                        lock (_gate)
                        {
                            if (_queue.Count == 0) { _running = false; return; }
                            next = _queue.Dequeue();
                        }
                        next();
                    }
                });
            }
            return completion.Task;
        }
    }

    public Task CompleteAsync() { lock (_gate) { _closed = true; return _worker; } }
}
