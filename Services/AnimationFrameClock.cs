using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

// Keep the deadline's phase when the display and target rates do not divide evenly.
internal partial struct AnimationFrameClock
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnCoreAnimClockState
    {
        public long LastTick;
        public double NextTick;
        public double TargetFps;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_anim_clock_try_advance(
        ref VnCoreAnimClockState ioState,
        long nowTick,
        long stopwatchFreq,
        double targetFps,
        out double outSeconds,
        out byte outAdvanced);

    private VnCoreAnimClockState _state;
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
        return vn_anim_clock_try_advance(ref _state, now, Stopwatch.Frequency, targetFps, out seconds, out byte advanced) == 0 && advanced != 0;
    }
}
