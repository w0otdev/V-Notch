namespace VNotch.Services;

// Players can remove an SMTC session while creating the next one. This is a
// bounded source-retention policy, not a wait for an operation to finish.
internal sealed class MediaPinReconnectWindow : IDisposable
{
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Action _onExpired;
    private ITimer? _timer;
    private long _started;
    private int _generation;
    private bool _waiting, _disposed;

    internal MediaPinReconnectWindow(Action onExpired, TimeProvider? clock = null)
    {
        _onExpired = onExpired;
        _clock = clock ?? TimeProvider.System;
    }

    internal bool IsWaiting { get { lock (_gate) return _waiting; } }

    internal bool HasExpired
    {
        get { lock (_gate) return _waiting && _clock.GetElapsedTime(_started) >= GracePeriod; }
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_disposed || _waiting) return;
            _waiting = true;
            _started = _clock.GetTimestamp();
            int generation = ++_generation;
            _timer = _clock.CreateTimer(_ => Expire(generation), null, GracePeriod, Timeout.InfiniteTimeSpan);
        }
    }

    private void Expire(int generation)
    {
        lock (_gate)
        {
            if (_disposed || !_waiting || generation != _generation || _timer == null) return;
            var remaining = GracePeriod - _clock.GetElapsedTime(_started);
            if (remaining > TimeSpan.Zero)
            {
                _timer?.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            _timer?.Dispose();
            _timer = null;
            // Reset and expiry are serialized, including callbacks already queued
            // when the timer was cancelled. The callback only wakes the detector.
            _onExpired();
        }
    }

    internal void Reset()
    {
        lock (_gate) ResetCore();
    }

    private void ResetCore()
    {
        _waiting = false;
        ++_generation;
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            ResetCore();
        }
    }
}
