using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Services;

public class MediaTimelineSimulator
{
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
    public BitmapImage? RecoveredThumbnail { get; set; }

    public void UpdateObservedPosition(TimeSpan position)
    {
        if (position != _lastObservedPosition)
        {
            _lastObservedPosition = position;
            _lastPositionChangeTime = DateTime.UtcNow;
        }
    }

    public bool IsPositionStuck(TimeSpan threshold)
    {
        return (DateTime.UtcNow - _lastPositionChangeTime).TotalSeconds > threshold.TotalSeconds;
    }

    public static bool IsAtEndStuck(double progress, DateTime lastMetadataChangeTime, TimeSpan threshold)
    {
        return progress > 0.98 && (DateTime.UtcNow - lastMetadataChangeTime) > threshold;
    }

    public void ApplySimulatedTimeline(MediaInfo info, bool atEndStuck)
    {
        var nowUtc = DateTime.UtcNow;
        EnsureSimulationBase(info, nowUtc);

        var elapsed = nowUtc - _simBaseWallTimeUtc;
        var sim = _simBasePosition + TimeSpan.FromSeconds(elapsed.TotalSeconds * _simBasePlaybackRate);

        if (!atEndStuck && info.Duration > TimeSpan.Zero && sim > info.Duration)
            sim = info.Duration;

        info.Position = sim;
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
        if (atEndStuck)
        {
            info.Duration = RecoveredDuration > TimeSpan.Zero ? RecoveredDuration : TimeSpan.Zero;
        }
        else if (info.Duration <= TimeSpan.Zero && RecoveredDuration > TimeSpan.Zero)
        {
            info.Duration = RecoveredDuration;
        }

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
