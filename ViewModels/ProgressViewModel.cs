using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class ProgressViewModel : ObservableObject
{
    private readonly IMediaDetectionService _mediaService;
    private DateTime _lastMediaUpdate = DateTime.Now;
    private TimeSpan _lastKnownPosition;
    private TimeSpan _lastKnownDuration;
    private DateTime _seekDebounceUntil = DateTime.MinValue;
    private bool _isDragging;
    private string _lastSignature = "";
    private long _formattedPositionSecond = long.MinValue;
    private long _formattedDurationSecond = long.MinValue;
    private string _formattedPosition = "";
    private string _formattedDuration = "";

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private string _currentTimeText = "0:00";

    [ObservableProperty]
    private string _remainingTimeText = "0:00";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _hasTimeline;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private bool _isSeekEnabled;

    [ObservableProperty]
    private MediaInfo? _currentMediaInfo;

    public Func<bool>? IsExpandedCheck { get; set; }
    public event EventHandler? SessionTransitioned;
    private string _lastSessionId = "";

    public ProgressViewModel(IMediaDetectionService mediaService) => _mediaService = mediaService;

    public void Update(MediaInfo info)
    {
        bool sessionChanged = !string.IsNullOrEmpty(info.SourceAppId) && info.SourceAppId != _lastSessionId;
        if (sessionChanged)
        {
            _lastSessionId = info.SourceAppId;
            SessionTransitioned?.Invoke(this, EventArgs.Empty);
        }

        CurrentMediaInfo = info;
        bool show = info.IsAnyMediaPlaying && (info.HasTimeline || info.IsIndeterminate);
        if (!show)
        {
            IsPlaying = false;
            _lastSessionId = "";
            IsIndeterminate = false;
            HasTimeline = true;
            _lastKnownDuration = _lastKnownPosition = TimeSpan.Zero;
            Position = 0;
            CurrentTimeText = RemainingTimeText = "0:00";
            if (IsExpandedCheck?.Invoke() == true) Render();
            return;
        }

        if (_isDragging) return;
        string signature = $"{info.SourceAppId}|{info.MediaSource}|{info.CurrentTrack}|{info.CurrentArtist}";
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            _seekDebounceUntil = DateTime.MinValue;
            if (info.Duration > TimeSpan.Zero) _lastKnownDuration = info.Duration;
            _lastKnownPosition = info.Position;
            _lastMediaUpdate = DateTime.Now;
        }

        if (DateTime.Now < _seekDebounceUntil)
        {
            IsPlaying = info.IsAnyMediaPlaying;
            if (info.Duration > TimeSpan.Zero) _lastKnownDuration = info.Duration;
            if (IsExpandedCheck?.Invoke() == true) Render();
            return;
        }

        if (info.HasTimeline)
        {
            if (info.Duration > TimeSpan.Zero) _lastKnownDuration = info.Duration;
            _lastKnownPosition = info.Position;
            _lastMediaUpdate = info.LastUpdated.LocalDateTime;
            IsIndeterminate = false;
            HasTimeline = true;
        }
        else
        {
            IsIndeterminate = info.IsIndeterminate;
            HasTimeline = false;
        }

        IsSeekEnabled = info.IsSeekEnabled;
        IsPlaying = info.IsPlaying;
        if (IsExpandedCheck?.Invoke() == true) Render();
    }

    public void Render()
    {
        if (_isDragging || CurrentMediaInfo == null) return;
        if (_lastKnownDuration <= TimeSpan.Zero)
        {
            CurrentTimeText = RemainingTimeText = "--:--";
            Position = 0;
            return;
        }

        var display = _lastKnownPosition;
        if (IsPlaying)
        {
            var elapsed = DateTime.Now - _lastMediaUpdate;
            double cap = CurrentMediaInfo.IsThrottled ? 3600 :
                CurrentMediaInfo.Platform is MediaPlatform.YouTube or MediaPlatform.Browser ? 600 : 30;
            if (elapsed > TimeSpan.FromSeconds(cap)) elapsed = TimeSpan.FromSeconds(cap);
            display += TimeSpan.FromTicks((long)(elapsed.Ticks * CurrentMediaInfo.PlaybackRate));
        }

        display = TimeSpan.FromTicks(Math.Clamp(display.Ticks, 0, _lastKnownDuration.Ticks));
        Position = display.TotalSeconds / _lastKnownDuration.TotalSeconds;
        CurrentTimeText = FormatCachedTime(display, ref _formattedPositionSecond, ref _formattedPosition);
        RemainingTimeText = FormatCachedTime(_lastKnownDuration, ref _formattedDurationSecond, ref _formattedDuration);
    }

    public void StartDragging() => _isDragging = true;
    public void StopDragging() => _isDragging = false;

    public void UpdateDragPosition(double ratio)
    {
        if (_lastKnownDuration <= TimeSpan.Zero) return;
        Position = Math.Clamp(ratio, 0, 1);
        CurrentTimeText = FormatCachedTime(TimeSpan.FromSeconds(_lastKnownDuration.TotalSeconds * Position),
            ref _formattedPositionSecond, ref _formattedPosition);
    }

    public TimeSpan GetDragSeekPosition(double ratio) =>
        TimeSpan.FromSeconds(_lastKnownDuration.TotalSeconds * Math.Clamp(ratio, 0, 1));

    public async Task SeekToPosition(TimeSpan position)
    {
        if (_lastKnownDuration <= TimeSpan.Zero) return;
        _lastKnownPosition = position;
        _lastMediaUpdate = DateTime.Now;
        _seekDebounceUntil = DateTime.Now.AddSeconds(2.5);
        if (IsExpandedCheck?.Invoke() == true) Render();
        try { await _mediaService.SeekAsync(position); }
        catch (Exception ex) { RuntimeLog.Error("VM-SEEK", ex.ToString()); }
    }

    public async Task SeekRelative(double seconds)
    {
        if (_lastKnownDuration <= TimeSpan.Zero) return;
        var elapsed = DateTime.Now - _lastMediaUpdate;
        var current = _lastKnownPosition + (IsPlaying ? elapsed : TimeSpan.Zero);
        var target = TimeSpan.FromTicks(Math.Clamp((current + TimeSpan.FromSeconds(seconds)).Ticks, 0, _lastKnownDuration.Ticks));
        _lastKnownPosition = target;
        _lastMediaUpdate = DateTime.Now;
        _seekDebounceUntil = DateTime.Now.AddSeconds(2.5);
        if (IsExpandedCheck?.Invoke() == true) Render();
        try { await _mediaService.SeekToAbsoluteAsync(target); }
        catch (Exception ex) { RuntimeLog.Error("VM-SEEK-RELATIVE", ex.ToString()); }
    }

    public bool ShouldShow => CurrentMediaInfo?.IsAnyMediaPlaying == true &&
        (CurrentMediaInfo.HasTimeline || CurrentMediaInfo.IsIndeterminate);

    private static string FormatCachedTime(TimeSpan time, ref long cachedSecond, ref string cachedText)
    {
        long second = time.Ticks / TimeSpan.TicksPerSecond;
        if (second != cachedSecond)
        {
            cachedText = FormatTime(time);
            cachedSecond = second;
        }
        return cachedText;
    }

    private static string FormatTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
