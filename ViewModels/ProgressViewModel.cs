using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class ProgressViewModel : ObservableObject
{
    private readonly IMediaDetectionService _mediaService;
    private DateTime _lastMediaUpdate = DateTime.UtcNow;
    private TimeSpan _lastKnownPosition;
    private TimeSpan _lastKnownDuration;
    private DateTime _seekDebounceUntil = DateTime.MinValue;
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

    public ProgressViewModel(IMediaDetectionService mediaService) => _mediaService = mediaService;

    public void Update(MediaInfo info)
    {
        CurrentMediaInfo = info;
        bool show = info.IsAnyMediaPlaying && (info.HasTimeline || info.IsTimelineIndeterminate);
        if (!show)
        {
            IsPlaying = false;
            IsIndeterminate = false;
            IsSeekEnabled = false;
            HasTimeline = true;
            _lastKnownDuration = _lastKnownPosition = TimeSpan.Zero;
            Position = 0;
            CurrentTimeText = RemainingTimeText = "0:00";
            if (IsExpandedCheck?.Invoke() == true) Render();
            return;
        }

        if (info.IsTimelineIndeterminate)
        {
            _seekDebounceUntil = DateTime.MinValue;
            _lastKnownDuration = _lastKnownPosition = TimeSpan.Zero;
            HasTimeline = false;
            IsIndeterminate = true;
            IsSeekEnabled = false;
            IsPlaying = info.IsPlaying;
            if (IsExpandedCheck?.Invoke() == true) Render();
            return;
        }

        bool timelineModeChanged = IsIndeterminate;
        IsIndeterminate = false;
        HasTimeline = true;
        IsSeekEnabled = info.CanSeekTimeline;
        if (timelineModeChanged) _seekDebounceUntil = DateTime.MinValue;
        string signature = $"{info.SourceAppId}|{info.MediaSource}|{info.CurrentTrack}|{info.CurrentArtist}";
        var nowUtc = DateTime.UtcNow;
        if (signature != _lastSignature)
        {
            _lastSignature = signature;
            _seekDebounceUntil = DateTime.MinValue;
            _lastKnownDuration = info.Duration;
            _lastKnownPosition = info.Position;
            _lastMediaUpdate = nowUtc;
        }

        if (nowUtc < _seekDebounceUntil)
        {
            IsPlaying = info.IsPlaying;
            _lastKnownDuration = info.Duration;
            if (IsExpandedCheck?.Invoke() == true) Render();
            return;
        }

        _lastKnownDuration = info.Duration;
        _lastKnownPosition = info.Position;
        _lastMediaUpdate = info.LastUpdated.UtcDateTime;
        IsPlaying = info.IsPlaying;
        if (IsExpandedCheck?.Invoke() == true) Render();
    }

    public void Render()
    {
        if (CurrentMediaInfo == null) return;
        if (IsIndeterminate)
        {
            CurrentTimeText = "";
            RemainingTimeText = CurrentMediaInfo.IsTimelinePending ? "--:--" : "LIVE";
            Position = 0;
            return;
        }
        if (_lastKnownDuration <= TimeSpan.Zero)
        {
            CurrentTimeText = RemainingTimeText = "--:--";
            Position = 0;
            return;
        }

        long elapsedTicks = (DateTime.UtcNow - _lastMediaUpdate).Ticks;
        byte isWebOrYouTube = CurrentMediaInfo.Platform is MediaPlatform.YouTube or MediaPlatform.Browser ? (byte)1 : (byte)0;
        vn_progress_interpolate_ticks(
            _lastKnownPosition.Ticks,
            _lastKnownDuration.Ticks,
            elapsedTicks,
            CurrentMediaInfo.PlaybackRate,
            IsPlaying ? (byte)1 : (byte)0,
            CurrentMediaInfo.IsThrottled ? (byte)1 : (byte)0,
            isWebOrYouTube,
            out long displayTicks,
            out double ratio);

        var display = TimeSpan.FromTicks(displayTicks);
        Position = ratio;
        CurrentTimeText = FormatCachedTime(display, ref _formattedPositionSecond, ref _formattedPosition);
        RemainingTimeText = FormatCachedTime(_lastKnownDuration, ref _formattedDurationSecond, ref _formattedDuration);
    }

    public async Task SeekToPosition(TimeSpan position)
    {
        if (CurrentMediaInfo?.CanSeekTimeline != true || _lastKnownDuration <= TimeSpan.Zero) return;
        _lastKnownPosition = position;
        var nowUtc = DateTime.UtcNow;
        _lastMediaUpdate = nowUtc;
        _seekDebounceUntil = nowUtc.AddSeconds(2.5);
        if (IsExpandedCheck?.Invoke() == true) Render();
        try { await _mediaService.SeekAsync(position); }
        catch (Exception ex) { RuntimeLog.Error("VM-SEEK", ex.ToString()); }
    }

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

    private static unsafe string FormatTime(TimeSpan time)
    {
        char* buf = stackalloc char[32];
        vn_progress_format_time(time.Ticks, buf, 32, out int len);
        return len > 0 ? new string(buf, 0, len) : "0:00";
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_progress_interpolate_ticks(
        long last_position_ticks,
        long duration_ticks,
        long elapsed_ticks,
        double playback_rate,
        byte is_playing,
        byte is_throttled,
        byte is_web_or_youtube,
        out long out_display_ticks,
        out double out_ratio);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_progress_format_time(
        long time_ticks, char* out_buf, int buf_cap, out int out_len);
}
