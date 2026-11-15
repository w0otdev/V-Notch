using System.Diagnostics;

namespace VNotch.Services;

// Keep the deadline's phase when the display and target rates do not divide evenly.
internal struct AnimationFrameClock
{
    private struct ClockState
    {
        public long LastTick;
        public double NextTick;
        public double TargetFps;
    }

    private ClockState _state;
    private TimeSpan _renderingTime;

    internal void Reset(long now)
    {
        _state.LastTick = now;
        _state.NextTick = now;
        _state.TargetFps = 0;
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
        // This scalar scheduler owns managed state; crossing P/Invoke for each
        // overlay/frame costs more work than the arithmetic itself.
        seconds = 0;
        targetFps = double.IsFinite(targetFps) ? Math.Max(1, targetFps) : 60;
        double frequency = Stopwatch.Frequency;
        double interval = frequency / targetFps;
        if (_state.TargetFps != targetFps)
        {
            _state.TargetFps = targetFps;
            _state.NextTick = _state.LastTick + interval;
        }
        if (now <= _state.LastTick || now + frequency * .0001 < _state.NextTick) return false;
        seconds = Math.Clamp((now - _state.LastTick) / frequency, 0, .05);
        _state.LastTick = now;
        _state.NextTick += Math.Max(1, Math.Floor((now - _state.NextTick) / interval) + 1) * interval;
        return true;
    }
}
