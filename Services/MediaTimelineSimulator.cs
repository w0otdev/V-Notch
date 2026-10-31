using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Services;

public partial class MediaTimelineSimulator
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timelinesim_is_at_end_stuck(
        double progress, long nowUtcTicks, long lastMetadataChangeTicks, long thresholdTicks,
        out byte outStuck);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_timelinesim_compute_position_ticks(
        long simBasePositionTicks,
        long simBaseWallUtcTicks,
        long nowUtcTicks,
        double playbackRate,
        long durationTicks,
        byte atEndStuck,
        out long outSimTicks);

    private DateTime _simBaseWallTimeUtc = DateTime.MinValue;
    private TimeSpan _simBasePosition = TimeSpan.Zero;
    private double _simBasePlaybackRate = 1.0;
    private string _simSignature = "";

    private bool _isThrottled;
    private TimeSpan _lastObservedPosition = TimeSpan.Zero;
    private DateTime _lastPositionChangeTime = DateTime.MinValue;

    public bool IsThrottled => _isThrottled;

    public TimeSpan LastObservedPosition => _lastObservedPosition;
    public DateTime LastPositionChangeTime => _lastPositionChangeTime;
    public TimeSpan RecoveredDuration { get; set; } = TimeSpan.Zero;
    public BitmapSource? RecoveredThumbnail { get; set; }

    public void UpdateObservedPosition(TimeSpan position)
    {
        if (position != _lastObservedPosition || _lastPositionChangeTime == DateTime.MinValue)
        {
            _lastObservedPosition = position;
            _lastPositionChangeTime = DateTime.UtcNow;
        }
    }

    public bool IsPositionStuck(TimeSpan threshold)
    {
        if (_lastPositionChangeTime == DateTime.MinValue)
            return false;

        return (DateTime.UtcNow - _lastPositionChangeTime).TotalSeconds > threshold.TotalSeconds;
    }

    public static bool IsAtEndStuck(double progress, DateTime lastMetadataChangeTime, TimeSpan threshold)
    {
        return vn_timelinesim_is_at_end_stuck(progress, DateTime.UtcNow.Ticks, lastMetadataChangeTime.Ticks, threshold.Ticks, out byte stuck) == 0 && stuck != 0;
    }

    public void ApplySimulatedTimeline(MediaInfo info, bool atEndStuck)
    {
        if (!info.HasTimeline)
        {
            Reset();
            info.IsThrottled = false;
            return;
        }

        var nowUtc = DateTime.UtcNow;
        EnsureSimulationBase(info, nowUtc);

        vn_timelinesim_compute_position_ticks(
            _simBasePosition.Ticks,
            _simBaseWallTimeUtc.Ticks,
            nowUtc.Ticks,
            _simBasePlaybackRate,
            info.Duration.Ticks,
            atEndStuck ? (byte)1 : (byte)0,
            out long simTicks);

        info.Position = TimeSpan.FromTicks(simTicks);
        info.IsThrottled = true;
        _isThrottled = true;

        ApplyRecoveredMetadata(info, atEndStuck);

        info.LastUpdated = DateTimeOffset.Now;
    }

    private void EnsureSimulationBase(MediaInfo info, DateTime nowUtc)
    {
        var sig = info.GetSignature();
        if (_simSignature != sig || _simBaseWallTimeUtc == DateTime.MinValue)
        {
            _simSignature = sig;
            _simBaseWallTimeUtc = nowUtc;
            _simBasePosition = _lastObservedPosition != TimeSpan.Zero ? _lastObservedPosition : info.Position;
            _simBasePlaybackRate = info.PlaybackRate > 0 ? info.PlaybackRate : 1.0;
        }
    }

    private void ApplyRecoveredMetadata(MediaInfo info, bool atEndStuck)
    {
        if (atEndStuck && MediaTimelinePolicy.HasFiniteDuration(RecoveredDuration))
            info.Duration = RecoveredDuration;
        if (info.Thumbnail == null && RecoveredThumbnail != null)
            info.Thumbnail = RecoveredThumbnail;
    }

    public void EnterThrottledMode()
    {
        _isThrottled = true;
    }

    public void Reset()
    {
        _isThrottled = false;
        _lastObservedPosition = TimeSpan.Zero;
        _lastPositionChangeTime = DateTime.MinValue;
        RecoveredDuration = TimeSpan.Zero;
        RecoveredThumbnail = null;
        ResetSimulation();
    }

    public void ResetSimulation()
    {
        _simBaseWallTimeUtc = DateTime.MinValue;
        _simBasePosition = TimeSpan.Zero;
        _simSignature = "";
    }

    public void ResetRecoveredData()
    {
        RecoveredDuration = TimeSpan.Zero;
        RecoveredThumbnail = null;
    }

    public bool TryExitThrottleIfPositionResumed(TimeSpan resumeThreshold)
    {
        if (!_isThrottled) return false;

        if ((DateTime.UtcNow - _lastPositionChangeTime) < resumeThreshold)
        {
            Reset();
            return true;
        }

        return false;
    }

    public bool TryExitThrottleIfStalled(TimeSpan stallThreshold)
    {
        if (!_isThrottled) return false;

        if ((DateTime.UtcNow - _lastPositionChangeTime).TotalSeconds > stallThreshold.TotalSeconds)
        {
            Reset();
            return true;
        }

        return false;
    }
}
