using System.Diagnostics;

namespace VNotch.Services;

// Keep the deadline's phase when the display and target rates do not divide evenly.
internal struct AnimationFrameClock
{
    private long _lastTick;
    private double _nextTick;
    private double _targetFps;
    private TimeSpan _renderingTime;

    internal void Reset(long now)
    {
        _lastTick = now;
        _nextTick = now;
        _targetFps = 0;
        _renderingTime = TimeSpan.MinValue;
    }

    internal bool TryAdvance(TimeSpan renderingTime, int targetFps, out double seconds)
    {
        seconds = 0;
        if (renderingTime == _renderingTime) return false;
        long now = (long)Math.Round(renderingTime.TotalSeconds * Stopwatch.Frequency);
        if (_renderingTime == TimeSpan.MinValue)
            Reset(now - Stopwatch.Frequency / Math.Max(1, targetFps));
        _renderingTime = renderingTime;
        return TryAdvance(now, targetFps, out seconds);
    }

    internal bool TryAdvance(long now, double targetFps, out double seconds)
    {
        seconds = 0;
        targetFps = double.IsFinite(targetFps) ? Math.Max(1, targetFps) : 60;
        double interval = Stopwatch.Frequency / (double)targetFps;
        if (_targetFps != targetFps)
        {
            _targetFps = targetFps;
            _nextTick = _lastTick + interval;
        }
        if (now <= _lastTick || now + Stopwatch.Frequency * .0001 < _nextTick) return false;

        seconds = Math.Clamp((now - _lastTick) / (double)Stopwatch.Frequency, 0, .05);
        _lastTick = now;
        // Drop missed deadlines after a stall; never queue catch-up frames.
        _nextTick += Math.Max(1, Math.Floor((now - _nextTick) / interval) + 1) * interval;
        return true;
    }
}
