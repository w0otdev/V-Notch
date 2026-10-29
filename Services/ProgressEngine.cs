using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

public enum ProgressState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Seeking,
    Stopped,
    Unknown
}

public struct ProgressSnapshot
{
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsPlaying { get; set; }
    public bool IsYouTube { get; set; }
    public double PlaybackRate { get; set; }
    public bool IsSeekEnabled { get; set; }
    public bool IsIndeterminate { get; set; }
    public DateTime Timestamp { get; set; }
    public long SequenceNumber { get; set; }
}

public struct UiProgressFrame
{
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public bool ShowIndeterminate { get; set; }
    public ProgressState State { get; set; }
    public bool DurationJustChanged { get; set; }
}

public partial class ProgressEngine
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_engine_normalize_playback_rate(
        double reportedRate,
        byte isYouTube,
        out double outRate);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_engine_effective_snapshot_position(
        long posTicks,
        long durTicks,
        byte isPlaying,
        byte isYouTube,
        byte isIndeterminateEffective,
        double playbackRate,
        long snapshotTsTicks,
        long nowUtcTicks,
        out long outEffectiveTicks);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_engine_predict_position(
        long basePosTicks,
        long baseTimeTicks,
        long nowUtcTicks,
        double playbackRate,
        byte isIndeterminate,
        byte isPlaying,
        int state,
        out long outPredictedTicks);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_engine_clamp_position(
        long posTicks,
        long durTicks,
        out long outClampedTicks);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_engine_did_duration_change(
        long prevTicks,
        long currTicks,
        byte isYouTube,
        out byte outChanged);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_engine_smooth_correction(
        long diffTicks,
        long predictedNowTicks,
        long durTicks,
        byte isYouTube,
        out long outCorrectedTicks);

    private const string LogReject = "PROGRESS-ENGINE-REJECT";
    private const string LogCorrect = "PROGRESS-ENGINE-CORRECT";

    private ProgressState _state = ProgressState.Idle;

    private TimeSpan _basePosition = TimeSpan.Zero;
    private DateTime _baseTimeUtc = DateTime.MinValue;
    private TimeSpan _duration = TimeSpan.Zero;
    private bool _isPlaying;

    private double _playbackRate = 1.0;
    private bool _isIndeterminate;
    private bool _isSeekEnabled;
    private bool _durationJustChanged;
    private bool _isYouTube;

    private DateTime _lastSnapshotTimestampUtc = DateTime.MinValue;
    private long _lastSnapshotSequence = -1;
    private TimeSpan _lastSnapshotPosition = TimeSpan.Zero;
    private bool _lastSnapshotIsPlaying;
    private DateTime _seekDebounceEndUtc = DateTime.MinValue;
    private DateTime _previousTrackRequestUntilUtc = DateTime.MinValue;
    private DateTime _seekPauseGraceUntilUtc = DateTime.MinValue;
    private bool _pendingPauseConfirmation;
    private DateTime _pendingPauseStartedUtc = DateTime.MinValue;
    private TimeSpan _pendingPausePosition = TimeSpan.Zero;

    private static readonly TimeSpan SnapshotOutOfOrderTolerance = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PauseBackstepTolerance = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan ResumeBackstepTolerance = TimeSpan.FromMilliseconds(350);

    private static readonly TimeSpan IgnoreCorrectionThreshold = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan SmoothCorrectionThreshold = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DurationChangeSeekWindow = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan UserSeekDebounceDuration = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan UserSeekPauseGraceDuration = TimeSpan.FromMilliseconds(3200);
    private static readonly TimeSpan PauseConfirmationWindow = TimeSpan.FromMilliseconds(700);

    private readonly object _lock = new();

    public void OnMediaSnapshot(ProgressSnapshot snapshot)
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime snapshotTsUtc = snapshot.Timestamp.Kind == DateTimeKind.Utc
                ? snapshot.Timestamp
                : snapshot.Timestamp.ToUniversalTime();
            TimeSpan effectiveSnapshotPosition = GetEffectiveSnapshotPosition(snapshot, snapshotTsUtc, nowUtc);

            LogSnapshotDiagnostics(snapshot, effectiveSnapshotPosition, snapshotTsUtc, nowUtc);

            if (IsSnapshotStaleOrDuplicate(snapshot, snapshotTsUtc))
            {
                return;
            }

            if (IsAnomalousBackwardJump(snapshot, effectiveSnapshotPosition, nowUtc))
            {
                return;
            }

            TimeSpan previousDuration = _duration;
            bool timelineModeChanged = HasTimelineModeChanged(snapshot);
            UpdateSnapshotMetadata(snapshot, snapshotTsUtc);

            if (timelineModeChanged)
            {
                // A VOD/live transition may keep its title, session and SMTC timestamp.
                // Rebase before seek/pause stabilization can retain the previous timeline.
                _durationJustChanged = true;
                _lastSnapshotTimestampUtc = snapshotTsUtc;
                _basePosition = ClampPosition(effectiveSnapshotPosition);
                _baseTimeUtc = nowUtc;
                _isPlaying = snapshot.IsPlaying;
                _state = snapshot.IsPlaying ? ProgressState.Playing : ProgressState.Paused;
                _seekDebounceEndUtc = DateTime.MinValue;
                _seekPauseGraceUntilUtc = DateTime.MinValue;
                _previousTrackRequestUntilUtc = DateTime.MinValue;
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
                _pendingPausePosition = TimeSpan.Zero;
                return;
            }

            if (HandleDurationChange(snapshot, previousDuration, effectiveSnapshotPosition, nowUtc))
            {
                return;
            }

            if (!snapshot.IsPlaying)
            {
                HandlePausedSnapshot(snapshot, nowUtc);
                return;
            }

            if (HandleResumeSnapshot(snapshot, effectiveSnapshotPosition, nowUtc))
            {
                return;
            }

            if (nowUtc < _seekDebounceEndUtc)
            {
                return;
            }

            if (_state == ProgressState.Seeking)
            {
                _state = ProgressState.Playing;
                _seekPauseGraceUntilUtc = DateTime.MinValue;
            }

            ApplyDriftCorrection(snapshot, effectiveSnapshotPosition, snapshotTsUtc, nowUtc);
        }
    }

    private void LogSnapshotDiagnostics(
        ProgressSnapshot snapshot,
        TimeSpan effectiveSnapshotPosition,
        DateTime snapshotTsUtc,
        DateTime nowUtc)
    {
        if (!RuntimeLog.IsEnabled(LogLevel.Debug)) return;
        TimeSpan snapshotAge = nowUtc - snapshotTsUtc;
        TimeSpan currentPredictedForLog = _baseTimeUtc != DateTime.MinValue ? PredictPosition(nowUtc) : TimeSpan.FromSeconds(-1);
        RuntimeLog.Log("PROGRESS-ENGINE-SNAP",
            $"seq={snapshot.SequenceNumber} rawPos={snapshot.Position.TotalSeconds:F2}s effectivePos={effectiveSnapshotPosition.TotalSeconds:F2}s " +
            $"dur={snapshot.Duration.TotalSeconds:F1}s playing={snapshot.IsPlaying} isBrowser={snapshot.IsYouTube} " +
            $"snapshotAge={snapshotAge.TotalSeconds:F2}s predicted={currentPredictedForLog.TotalSeconds:F2}s " +
            $"basePos={_basePosition.TotalSeconds:F2}s state={_state} rate={snapshot.PlaybackRate:F2}");
    }

    private bool IsSnapshotStaleOrDuplicate(ProgressSnapshot snapshot, DateTime snapshotTsUtc)
    {
        if (snapshot.SequenceNumber > 0 && snapshot.SequenceNumber <= _lastSnapshotSequence)
        {
            RuntimeLog.Log(LogReject, $"out-of-order seq={snapshot.SequenceNumber} <= last={_lastSnapshotSequence}");
            return true;
        }

        bool playStateChanged = snapshot.IsPlaying != _lastSnapshotIsPlaying;
        bool timelineModeChanged = HasTimelineModeChanged(snapshot);
        bool stoppingActivePlayback = !snapshot.IsPlaying && (_isPlaying || _state == ProgressState.Seeking);

        if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
            !playStateChanged &&
            !timelineModeChanged &&
            !stoppingActivePlayback &&
            snapshotTsUtc < _lastSnapshotTimestampUtc - SnapshotOutOfOrderTolerance)
        {
            RuntimeLog.Log(LogReject, $"timestamp out-of-order: snapshot={snapshotTsUtc:O} < last={_lastSnapshotTimestampUtc:O}");
            return true;
        }

        if (_lastSnapshotTimestampUtc != DateTime.MinValue &&
            !playStateChanged &&
            !timelineModeChanged &&
            !stoppingActivePlayback &&
            snapshot.Duration == _duration &&
            snapshot.IsSeekEnabled == _isSeekEnabled &&
            snapshotTsUtc == _lastSnapshotTimestampUtc &&
            Math.Abs((snapshot.Position - _lastSnapshotPosition).TotalSeconds) < 0.01)
        {
            RuntimeLog.Log(LogReject, $"duplicate snapshot: ts={snapshotTsUtc:O} pos={snapshot.Position.TotalSeconds:F2}s");
            return true;
        }

        return false;
    }

    private bool IsAnomalousBackwardJump(ProgressSnapshot snapshot, TimeSpan effectiveSnapshotPosition, DateTime nowUtc)
    {
        if (HasTimelineModeChanged(snapshot) || _isIndeterminate ||
            _baseTimeUtc == DateTime.MinValue || !_isPlaying || _duration <= TimeSpan.Zero)
        {
            return false;
        }

        var currentPredicted = PredictPosition(nowUtc);
        var backwardDiff = currentPredicted - effectiveSnapshotPosition;
        bool isExpectedPreviousTrackJump = nowUtc < _previousTrackRequestUntilUtc;

        bool likelySessionSwitch = snapshot.Duration > TimeSpan.Zero &&
            Math.Abs((snapshot.Duration - _duration).TotalSeconds) > Math.Max(5.0, _duration.TotalSeconds * 0.1);

        bool likelyUserSeekBackward = backwardDiff.TotalSeconds > 5.0 || _state == ProgressState.Seeking;

        bool isFalseZeroGlitch = effectiveSnapshotPosition.TotalSeconds < 1.0 &&
            currentPredicted.TotalSeconds > 15.0 &&
            _duration.TotalSeconds > 0;

        bool predictedAtTrackEnd = currentPredicted.TotalSeconds >= _duration.TotalSeconds - 2.0;

        if (isFalseZeroGlitch && !predictedAtTrackEnd && !isExpectedPreviousTrackJump && _state != ProgressState.Seeking)
        {
            RuntimeLog.Log(LogReject,
                $"false-zero-glitch: predicted={currentPredicted.TotalSeconds:F2}s snapshot={effectiveSnapshotPosition.TotalSeconds:F2}s " +
                $"rawPos={snapshot.Position.TotalSeconds:F2}s — rejecting SMTC zero report");
            return true;
        }

        double backwardThreshold = snapshot.IsYouTube ? 2.0 : 0.5;

        if (backwardDiff.TotalSeconds > backwardThreshold
            && !likelySessionSwitch
            && !likelyUserSeekBackward
            && !isExpectedPreviousTrackJump)
        {
            RuntimeLog.Log(LogReject,
                $"backward-guard: predicted={currentPredicted.TotalSeconds:F2}s snapshot={effectiveSnapshotPosition.TotalSeconds:F2}s " +
                $"backwardDiff={backwardDiff.TotalSeconds:F2}s threshold={backwardThreshold:F1}s " +
                $"sessionSwitch={likelySessionSwitch} userSeek={likelyUserSeekBackward}");
            return true;
        }

        return false;
    }

    private void UpdateSnapshotMetadata(ProgressSnapshot snapshot, DateTime snapshotTsUtc)
    {
        if (snapshotTsUtc > _lastSnapshotTimestampUtc || (!snapshot.IsPlaying && _lastSnapshotIsPlaying))
        {
            _lastSnapshotTimestampUtc = snapshotTsUtc;
        }

        if (snapshot.SequenceNumber > _lastSnapshotSequence)
        {
            _lastSnapshotSequence = snapshot.SequenceNumber;
        }

        _lastSnapshotPosition = snapshot.Position;
        _lastSnapshotIsPlaying = snapshot.IsPlaying;

        _duration = MediaTimelinePolicy.HasFiniteDuration(snapshot.Duration) ? snapshot.Duration : TimeSpan.Zero;
        _isIndeterminate = MediaTimelinePolicy.IsIndeterminate(snapshot.Duration, snapshot.IsIndeterminate);
        _isSeekEnabled = snapshot.IsSeekEnabled;
        _isYouTube = snapshot.IsYouTube;

        vn_progress_engine_normalize_playback_rate(snapshot.PlaybackRate, snapshot.IsYouTube ? (byte)1 : (byte)0, out _playbackRate);

        if (snapshot.IsPlaying && _pendingPauseConfirmation)
        {
            _pendingPauseConfirmation = false;
            _pendingPauseStartedUtc = DateTime.MinValue;
        }
    }

    private bool HandleDurationChange(ProgressSnapshot snapshot, TimeSpan previousDuration, TimeSpan effectiveSnapshotPosition, DateTime nowUtc)
    {
        bool durationChanged = !_isIndeterminate && DidDurationChange(previousDuration, _duration);
        _durationJustChanged |= durationChanged;

        if (durationChanged && snapshot.IsPlaying)
        {
            TimeSpan predictedAtDurationChange = ClampPosition(PredictPosition(nowUtc));
            bool likelyTrackRestart = effectiveSnapshotPosition.TotalSeconds <=
                Math.Min(5.0, _duration.TotalSeconds > 0 ? _duration.TotalSeconds * 0.15 : 5.0);
            bool predictedOutsideNewDuration =
                _duration.TotalSeconds > 0 &&
                predictedAtDurationChange > _duration + TimeSpan.FromSeconds(1);

            if (likelyTrackRestart || predictedOutsideNewDuration)
            {
                TimeSpan resetPos = ClampPosition(effectiveSnapshotPosition);
                _basePosition = resetPos;
                _baseTimeUtc = nowUtc;
                _isPlaying = true;
                _state = ProgressState.Playing;
                _seekDebounceEndUtc = nowUtc + DurationChangeSeekWindow;
                return true;
            }
        }

        return false;
    }

    private void HandlePausedSnapshot(ProgressSnapshot snapshot, DateTime nowUtc)
    {
        if (HandleSeekStabilizationDuringPause(snapshot, nowUtc))
        {
            return;
        }

        TimeSpan pausedPos = ClampPosition(snapshot.Position);
        if (!_isIndeterminate && ProcessPendingPauseConfirmation(ref pausedPos, nowUtc))
        {
            return;
        }

        _isPlaying = false;
        _state = ProgressState.Paused;
        _basePosition = pausedPos;
        _baseTimeUtc = nowUtc;

        _seekDebounceEndUtc = DateTime.MinValue;
        _seekPauseGraceUntilUtc = DateTime.MinValue;
        _pendingPauseConfirmation = false;
        _pendingPauseStartedUtc = DateTime.MinValue;
    }

    private bool HandleSeekStabilizationDuringPause(ProgressSnapshot snapshot, DateTime nowUtc)
    {
        bool inSeekStabilizationWindow = nowUtc < _seekDebounceEndUtc || nowUtc < _seekPauseGraceUntilUtc;
        if (inSeekStabilizationWindow && _state == ProgressState.Seeking)
        {
            TimeSpan observedDuringSeek = ClampPosition(snapshot.Position);
            if (observedDuringSeek > _basePosition)
            {
                _basePosition = observedDuringSeek;
            }
            _isPlaying = false;
            _baseTimeUtc = nowUtc;
            return true;
        }

        return false;
    }

    private bool ProcessPendingPauseConfirmation(ref TimeSpan pausedPos, DateTime nowUtc)
    {
        if (!_isPlaying && _state != ProgressState.Playing && _state != ProgressState.Seeking)
        {
            _pendingPauseConfirmation = false;
            _pendingPauseStartedUtc = DateTime.MinValue;
            return false;
        }

        TimeSpan predictedAtPending = ClampPosition(PredictPosition(nowUtc));
        if (pausedPos < predictedAtPending - PauseBackstepTolerance)
        {
            pausedPos = predictedAtPending;
        }

        if (!_pendingPauseConfirmation)
        {
            _pendingPauseConfirmation = true;
            _pendingPauseStartedUtc = nowUtc;
            _pendingPausePosition = pausedPos;

            _isPlaying = false;
            _basePosition = pausedPos;
            _baseTimeUtc = nowUtc;
            return true;
        }

        _pendingPausePosition = pausedPos;
        _isPlaying = false;
        _basePosition = pausedPos;
        _baseTimeUtc = nowUtc;

        var pendingAge = nowUtc - _pendingPauseStartedUtc;
        if (pendingAge < PauseConfirmationWindow)
        {
            return true;
        }

        pausedPos = _pendingPausePosition;
        return false;
    }

    private bool HandleResumeSnapshot(ProgressSnapshot snapshot, TimeSpan effectiveSnapshotPosition, DateTime nowUtc)
    {
        if (_isPlaying && (_state == ProgressState.Playing || _state == ProgressState.Seeking))
        {
            return false;
        }

        TimeSpan startPos = ClampPosition(effectiveSnapshotPosition);
        if ((_state == ProgressState.Paused || _state == ProgressState.Seeking) && startPos < _basePosition - ResumeBackstepTolerance)
        {
            RuntimeLog.Log("PROGRESS-ENGINE-RESUME",
                $"backstep-blocked: startPos={startPos.TotalSeconds:F2}s < basePos={_basePosition.TotalSeconds:F2}s " +
                $"tolerance={ResumeBackstepTolerance.TotalMilliseconds}ms -> using basePos");
            startPos = _basePosition;
        }

        if (_duration > TimeSpan.Zero &&
            snapshot.Position.TotalSeconds < 5.0 &&
            startPos.TotalSeconds > _duration.TotalSeconds * 0.8)
        {
            RuntimeLog.Log("PROGRESS-ENGINE-RESUME",
                $"stale-compensation-guard: rawPos={snapshot.Position.TotalSeconds:F2}s effective={startPos.TotalSeconds:F2}s " +
                $"> 80% of dur={_duration.TotalSeconds:F1}s -> using rawPos");
            startPos = ClampPosition(snapshot.Position);
        }

        RuntimeLog.Log("PROGRESS-ENGINE-RESUME",
            $"play-start: pos={startPos.TotalSeconds:F2}s prevState={_state} prevBase={_basePosition.TotalSeconds:F2}s");

        _isPlaying = true;
        _state = _state == ProgressState.Seeking && nowUtc < _seekDebounceEndUtc
            ? ProgressState.Seeking
            : ProgressState.Playing;
        _basePosition = startPos;
        _baseTimeUtc = nowUtc;
        _seekPauseGraceUntilUtc = DateTime.MinValue;
        _pendingPauseConfirmation = false;
        _pendingPauseStartedUtc = DateTime.MinValue;

        return true;
    }

    private void ApplyDriftCorrection(ProgressSnapshot snapshot, TimeSpan effectiveSnapshotPosition, DateTime snapshotTsUtc, DateTime nowUtc)
    {
        TimeSpan observedPos = ClampPosition(effectiveSnapshotPosition);
        TimeSpan predictedNow = ClampPosition(PredictPosition(nowUtc));
        TimeSpan diff = observedPos - predictedNow;
        double absDiffSeconds = Math.Abs(diff.TotalSeconds);

        if (absDiffSeconds < IgnoreCorrectionThreshold.TotalSeconds)
        {
            return;
        }

        if (absDiffSeconds < SmoothCorrectionThreshold.TotalSeconds)
        {
            ApplySmoothCorrection(diff, predictedNow, nowUtc);
            return;
        }

        RuntimeLog.Log(LogCorrect,
            $"*** LARGE-SNAP: predicted={predictedNow.TotalSeconds:F2}s observed={observedPos.TotalSeconds:F2}s " +
            $"diff={diff.TotalSeconds:F2}s direction={(diff < TimeSpan.Zero ? "BACKWARD" : "forward")} " +
            $"rawSnapshotPos={snapshot.Position.TotalSeconds:F2}s snapshotAge={(nowUtc - snapshotTsUtc).TotalSeconds:F2}s " +
            $"isBrowser={_isYouTube} dur={_duration.TotalSeconds:F1}s");

        _basePosition = observedPos;
        _baseTimeUtc = nowUtc;
        _state = ProgressState.Playing;
        _seekDebounceEndUtc = nowUtc + TimeSpan.FromMilliseconds(600);
        _seekPauseGraceUntilUtc = DateTime.MinValue;
        _pendingPauseConfirmation = false;
        _pendingPauseStartedUtc = DateTime.MinValue;
    }

    private void ApplySmoothCorrection(TimeSpan diff, TimeSpan predictedNow, DateTime nowUtc)
    {
        vn_progress_engine_smooth_correction(
            diff.Ticks,
            predictedNow.Ticks,
            _duration.Ticks,
            _isYouTube ? (byte)1 : (byte)0,
            out long correctedTicks);
        _basePosition = TimeSpan.FromTicks(correctedTicks);
        _baseTimeUtc = nowUtc;
    }

    public void NotifyUserSeek(TimeSpan position, bool? isPlaying = null)
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan clampedPos = ClampPosition(position);

            bool shouldPlay = isPlaying ?? (_isPlaying && _state != ProgressState.Paused && _state != ProgressState.Stopped);
            _state = ProgressState.Seeking;
            _isPlaying = shouldPlay;
            _basePosition = clampedPos;
            _baseTimeUtc = nowUtc;
            _seekDebounceEndUtc = nowUtc + UserSeekDebounceDuration;
            _seekPauseGraceUntilUtc = nowUtc + UserSeekPauseGraceDuration;
            _pendingPauseConfirmation = false;
            _pendingPauseStartedUtc = DateTime.MinValue;
        }
    }

    public void NotifyPreviousTrackRequested()
    {
        lock (_lock)
        {
            // Arm acceptance of a confirmed backward/zero snapshot without
            // changing the displayed position optimistically.
            _previousTrackRequestUntilUtc = DateTime.UtcNow.AddSeconds(5);
        }
    }

    public void NotifyUserPlayPause(bool isPlaying)
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;

            if (!isPlaying && _isPlaying)
            {
                _basePosition = ClampPosition(PredictPosition(nowUtc));
                _baseTimeUtc = nowUtc;
                _isPlaying = false;
                _state = ProgressState.Paused;
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
            }
            else if (isPlaying && !_isPlaying)
            {
                _baseTimeUtc = nowUtc;
                _isPlaying = true;
                _state = ProgressState.Playing;
                _pendingPauseConfirmation = false;
                _pendingPauseStartedUtc = DateTime.MinValue;
            }
        }
    }

    private static TimeSpan GetEffectiveSnapshotPosition(ProgressSnapshot snapshot, DateTime snapshotTsUtc, DateTime nowUtc)
    {
        bool indeterminateEffective = MediaTimelinePolicy.IsIndeterminate(snapshot.Duration, snapshot.IsIndeterminate);
        vn_progress_engine_effective_snapshot_position(
            snapshot.Position.Ticks,
            snapshot.Duration.Ticks,
            snapshot.IsPlaying ? (byte)1 : (byte)0,
            snapshot.IsYouTube ? (byte)1 : (byte)0,
            indeterminateEffective ? (byte)1 : (byte)0,
            snapshot.PlaybackRate,
            snapshotTsUtc == DateTime.MinValue ? 0 : snapshotTsUtc.Ticks,
            nowUtc.Ticks,
            out long effectiveTicks);
        return TimeSpan.FromTicks(effectiveTicks);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = ProgressState.Idle;
            _basePosition = TimeSpan.Zero;
            _baseTimeUtc = DateTime.MinValue;
            _duration = TimeSpan.Zero;
            _isPlaying = false;
            _playbackRate = 1.0;
            _isIndeterminate = false;
            _isSeekEnabled = false;
            _durationJustChanged = false;
            _isYouTube = false;
            _lastSnapshotTimestampUtc = DateTime.MinValue;
            _lastSnapshotSequence = -1;
            _lastSnapshotPosition = TimeSpan.Zero;
            _lastSnapshotIsPlaying = false;
            _seekDebounceEndUtc = DateTime.MinValue;
            _previousTrackRequestUntilUtc = DateTime.MinValue;
            _seekPauseGraceUntilUtc = DateTime.MinValue;
            _pendingPauseConfirmation = false;
            _pendingPauseStartedUtc = DateTime.MinValue;
            _pendingPausePosition = TimeSpan.Zero;
        }
    }

    internal bool RequiresPositionUpdates
    {
        get
        {
            lock (_lock)
                return !_isIndeterminate && ((_isPlaying && _state == ProgressState.Playing) || _state == ProgressState.Seeking);
        }
    }

    public UiProgressFrame GetUiFrame()
    {
        lock (_lock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (_pendingPauseConfirmation && (nowUtc - _pendingPauseStartedUtc) >= PauseConfirmationWindow)
            {
                _pendingPauseConfirmation = false;
                _state = ProgressState.Paused;
            }

            if (_state == ProgressState.Seeking &&
                !_isPlaying &&
                nowUtc >= _seekDebounceEndUtc &&
                nowUtc >= _seekPauseGraceUntilUtc)
            {
                _state = ProgressState.Paused;
            }

            TimeSpan pos = ClampPosition(PredictPosition(nowUtc));

            bool durationChanged = _durationJustChanged;
            _durationJustChanged = false;

            ProgressState uiState = (_state == ProgressState.Seeking || _state == ProgressState.Playing) && !_isPlaying
                ? ProgressState.Paused
                : _state;

            return new UiProgressFrame
            {
                Position = pos,
                Duration = _duration,
                ShowIndeterminate = _isIndeterminate,
                State = uiState,
                DurationJustChanged = durationChanged
            };
        }
    }

    private TimeSpan PredictPosition(DateTime nowUtc)
    {
        vn_progress_engine_predict_position(
            _basePosition.Ticks,
            _baseTimeUtc == DateTime.MinValue ? 0 : _baseTimeUtc.Ticks,
            nowUtc.Ticks,
            _playbackRate,
            _isIndeterminate ? (byte)1 : (byte)0,
            _isPlaying ? (byte)1 : (byte)0,
            (int)_state,
            out long predictedTicks);
        return TimeSpan.FromTicks(predictedTicks);
    }

    private bool DidDurationChange(TimeSpan previous, TimeSpan current)
    {
        vn_progress_engine_did_duration_change(
            previous.Ticks,
            current.Ticks,
            _isYouTube ? (byte)1 : (byte)0,
            out byte changed);
        return changed != 0;
    }

    private bool HasTimelineModeChanged(ProgressSnapshot snapshot) =>
        _baseTimeUtc != DateTime.MinValue &&
        _isIndeterminate != MediaTimelinePolicy.IsIndeterminate(snapshot.Duration, snapshot.IsIndeterminate);

    private TimeSpan ClampPosition(TimeSpan pos)
    {
        vn_progress_engine_clamp_position(pos.Ticks, _duration.Ticks, out long clampedTicks);
        return TimeSpan.FromTicks(clampedTicks);
    }
}
