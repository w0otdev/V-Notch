using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>Serializes blocking device writes, retaining only the latest pending value per control.</summary>
internal sealed partial class CoalescingActionQueue : IDisposable
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, EntryPoint = "vn_coalescing_action_queue_evaluate_post")]
    private static partial int NativeEvaluatePost(
        byte keyAlreadyPending,
        byte isRunning,
        out byte outShouldEnqueueKey,
        out byte outShouldStartWorker);

    private readonly object _gate = new();
    private readonly Dictionary<string, Action> _pending = new();
    private readonly Queue<string> _keys = new();
    private bool _running;
    private bool _disposed;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
            _keys.Clear();
        }
    }

    public void Post(string key, Action action)
    {
        lock (_gate)
        {
            if (_disposed) return;
            NativeEvaluatePost(
                _pending.ContainsKey(key) ? (byte)1 : (byte)0,
                _running ? (byte)1 : (byte)0,
                out byte shouldEnqueueKey,
                out byte shouldStartWorker);
            if (shouldEnqueueKey != 0) _keys.Enqueue(key);
            _pending[key] = action;
            if (shouldStartWorker == 0) return;
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
                if (_disposed || _pending.Count == 0)
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
