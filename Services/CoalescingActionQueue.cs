namespace VNotch.Services;

/// <summary>Serializes blocking device writes, retaining only the latest pending value per control.</summary>
internal sealed class CoalescingActionQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Action> _pending = new();
    private readonly Queue<string> _keys = new();
    private bool _running;

    public void Post(string key, Action action)
    {
        lock (_gate)
        {
            if (!_pending.ContainsKey(key)) _keys.Enqueue(key);
            _pending[key] = action;
            if (_running) return;
            _running = true;
        }
        ThreadPool.QueueUserWorkItem(_ => Drain());
    }

    private void Drain()
    {
        while (true)
        {
            Action action;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _running = false;
                    return;
                }
                string key = _keys.Dequeue();
                action = _pending[key];
                _pending.Remove(key);
            }
            try { action(); }
            catch (Exception ex) { RuntimeLog.Error("AUDIO-WRITE", ex, "Background device operation failed"); }
        }
    }
}
