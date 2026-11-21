using System;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public class ShellViewModelProgressTests
{
    private readonly FakeMediaDetectionService _media = new();
    private readonly FakeVolumeService _volume = new(isAvailable: true);
    private readonly ShellViewModel _vm;

    public ShellViewModelProgressTests()
    {
        _vm = new ShellViewModel(
            _media,
            _volume,
            new FakeBatteryService(),
            new FakeDispatcherService());

        _vm.Progress.IsExpandedCheck = () => true;
    }

    private static MediaInfo Timeline(
        string source,
        double durationSeconds,
        double positionSeconds = 0,
        bool playing = true,
        bool throttled = false,
        DateTimeOffset? lastUpdated = null,
        string track = "T",
        string artist = "Artist",
        string appId = "app1")
        => new MediaInfo
        {
            IsAnyMediaPlaying = true,
            IsPlaying = playing,
            IsThrottled = throttled,
            IsIndeterminate = false,
            IsSeekEnabled = true,
            CurrentTrack = track,
            CurrentArtist = artist,
            MediaSource = source,
            SourceAppId = appId,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Position = TimeSpan.FromSeconds(positionSeconds),
            PlaybackRate = 1.0,
            LastUpdated = lastUpdated ?? DateTimeOffset.Now
        };

    [Fact]
    public void RenderProgressBar_DefaultSource_CapsPredictionAt30Seconds()
    {
        var info = Timeline("Spotify", durationSeconds: 100_000, positionSeconds: 0,
            lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal("0:30", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_YouTube_CapsPredictionAt600Seconds()
    {
        var info = Timeline("YouTube", durationSeconds: 100_000, positionSeconds: 0,
            lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal("10:00", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_Browser_CapsPredictionAt600Seconds()
    {
        var info = Timeline("Browser", durationSeconds: 100_000, positionSeconds: 0,
            lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal("10:00", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_Throttled_CapsPredictionAt3600Seconds()
    {
        var info = Timeline("Spotify", durationSeconds: 100_000, positionSeconds: 0,
            throttled: true, lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal("1:00:00", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_PredictionPastDuration_ClampsRatioToOne()
    {
        var info = Timeline("Spotify", durationSeconds: 10, positionSeconds: 0,
            throttled: true, lastUpdated: DateTimeOffset.Now.AddHours(-1));

        _media.RaiseMediaChanged(info);

        Assert.Equal(1.0, _vm.Progress.Position, 3);
        Assert.Equal("0:10", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public void RenderProgressBar_NegativePausedPosition_ClampsToZero()
    {
        var info = Timeline("Spotify", durationSeconds: 100, positionSeconds: -5, playing: false,
            lastUpdated: DateTimeOffset.Now);

        _media.RaiseMediaChanged(info);

        Assert.Equal(0.0, _vm.Progress.Position, 3);
        Assert.Equal("0:00", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public void FormatTime_UnderOneHour_UsesMinuteSecondFormat()
    {
        var info = Timeline("Spotify", durationSeconds: 90, positionSeconds: 0, playing: false,
            lastUpdated: DateTimeOffset.Now);

        _media.RaiseMediaChanged(info);

        Assert.Equal("1:30", _vm.Progress.RemainingTimeText);
    }

    [Fact]
    public void FormatTime_OverOneHour_UsesHourMinuteSecondFormat()
    {
        var info = Timeline("Spotify", durationSeconds: 3661, positionSeconds: 0, playing: false,
            lastUpdated: DateTimeOffset.Now);

        _media.RaiseMediaChanged(info);

        Assert.Equal("1:01:01", _vm.Progress.RemainingTimeText);
    }

    [Fact]
    public void RenderProgressBar_ZeroDuration_ShowsLive()
    {
        var info = new MediaInfo
        {
            IsAnyMediaPlaying = true,
            IsPlaying = true,
            IsIndeterminate = true,
            CurrentTrack = "T",
            CurrentArtist = "Artist",
            MediaSource = "Spotify",
            SourceAppId = "app1",
            Duration = TimeSpan.Zero,
            Position = TimeSpan.Zero,
            LastUpdated = DateTimeOffset.Now
        };

        _media.RaiseMediaChanged(info);

        Assert.Equal("", _vm.Progress.CurrentTimeText);
        Assert.Equal("LIVE", _vm.Progress.RemainingTimeText);
        Assert.Equal(0.0, _vm.Progress.Position, 3);
    }

    [Fact]
    public async Task SeekDebounce_IgnoresIncomingPositionWithinWindow()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));
        Assert.Equal("0:10", _vm.Progress.CurrentTimeText);

        await _vm.Progress.SeekToPosition(TimeSpan.FromSeconds(50));
        Assert.Equal(TimeSpan.FromSeconds(50), _media.LastSeek);
        Assert.Equal("0:50", _vm.Progress.CurrentTimeText);

        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));

        Assert.Equal("0:50", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public async Task SeekDebounce_AcceptsIncomingPositionAfterWindowExpires()
    {
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));

        await _vm.Progress.SeekToPosition(TimeSpan.FromSeconds(50));
        Assert.Equal("0:50", _vm.Progress.CurrentTimeText);

        await Task.Delay(2600);

        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 10, playing: false,
            lastUpdated: DateTimeOffset.Now));

        Assert.Equal("0:10", _vm.Progress.CurrentTimeText);
    }

    [Fact]
    public void SetVolumeFromRatio_MidRange_SetsVolumeAndIcon()
    {
        _vm.AudioMixer.SetVolume(0.5f);

        Assert.Equal(0.5f, _vm.AudioMixer.CurrentVolume);
        Assert.Equal(0.5f, _volume.LastSetVolume);
        Assert.Equal("\uE994", _vm.AudioMixer.IconText);
    }

    [Fact]
    public void SetVolumeFromRatio_ClampsAboveOne()
    {
        _vm.AudioMixer.SetVolume(1.5f);

        Assert.Equal(1.0f, _vm.AudioMixer.CurrentVolume);
        Assert.Equal("\uE995", _vm.AudioMixer.IconText);
    }

    [Fact]
    public void SetVolumeFromRatio_ClampsBelowZero_ShowsMutedIcon()
    {
        _vm.AudioMixer.SetVolume(-0.5f);

        Assert.Equal(0f, _vm.AudioMixer.CurrentVolume);
        Assert.Equal("\uE74F", _vm.AudioMixer.IconText);
    }

    [Fact]
    public void SetVolumeFromRatio_LowRange_ShowsLowIcon()
    {
        _vm.AudioMixer.SetVolume(0.1f);

        Assert.Equal("\uE993", _vm.AudioMixer.IconText);
    }

    [Fact]
    public void ViewCommands_OwnShellState()
    {
        _vm.OpenTimerCommand.Execute(null);

        Assert.Equal(NotchView.Timer, _vm.CurrentView);
        Assert.True(_vm.IsExpanded);

        _vm.CollapseCommand.Execute(null);

        Assert.Equal(NotchView.Compact, _vm.CurrentView);
        Assert.False(_vm.IsExpanded);
    }

    [Fact]
    public void FeatureState_IsOwnedByChildViewModels()
    {
        _vm.AudioMixer.SetVolume(0.5f);
        _media.RaiseMediaChanged(Timeline("Spotify", 100, positionSeconds: 25, playing: false));

        Assert.Equal(0.5f, _vm.AudioMixer.CurrentVolume);
        Assert.Equal(0.25, _vm.Progress.Position, 2);
        Assert.Equal("T", _vm.Media.Title);
    }

    [Fact]
    public void Timer_AdjustTickAndReset_OwnCountdownState()
    {
        var timer = _vm.Timer;
        Assert.True(timer.Adjust(1));
        Assert.Equal(TimeSpan.FromMinutes(26), timer.Duration);

        timer.Start();
        Assert.False(timer.Tick(TimeSpan.FromMinutes(1)));
        Assert.Equal("25:00", timer.DisplayText);
        Assert.InRange(timer.Progress, 0.038, 0.039);

        timer.Reset();
        Assert.False(timer.IsRunning);
        Assert.Equal(timer.Duration, timer.Remaining);
    }

    [Fact]
    public void Timer_CompletionStopsAtZero()
    {
        var timer = _vm.Timer;
        timer.Duration = TimeSpan.FromMinutes(1);
        timer.Reset();
        timer.Start();

        Assert.True(timer.Tick(TimeSpan.FromMinutes(2)));
        Assert.Equal(TimeSpan.Zero, timer.Remaining);
        Assert.False(timer.IsRunning);
        Assert.Equal(1, timer.Progress);
    }

    [Fact]
    public void Timer_TickNotifiesBoundDisplayProperties()
    {
        var timer = _vm.Timer;
        var changed = new HashSet<string?>();
        timer.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        timer.Start();

        timer.Tick(TimeSpan.FromSeconds(1));

        Assert.Contains(nameof(TimerViewModel.DisplayText), changed);
        Assert.Contains(nameof(TimerViewModel.Progress), changed);
    }

    [Fact]
    public void AudioMixerSnapshot_OwnsStructureAndSessionState()
    {
        var snapshot = new AudioMixerSnapshot
        {
            Output = [new AudioDeviceInfo { Id = "out", FriendlyName = "Speakers X", IsDefault = true }],
            Input = [new AudioDeviceInfo { Id = "in", FriendlyName = "Mic X", IsDefault = true }],
            Sessions = [new AudioSessionInfo { ProcessId = 42, DisplayName = "Player", Volume = 0, IsMuted = true }]
        };
        _vm.AudioMixer.Snapshot = snapshot;

        _vm.AudioMixer.CacheSessionVolume(42, 0.75f);

        Assert.Equal("Speakers X", snapshot.GetDefaultOutputName());
        Assert.Equal("Mic X", snapshot.GetDefaultInputName());
        Assert.Equal("42|out|in|1|1", snapshot.StructureKey);
        Assert.Equal(0.75f, snapshot.Sessions[0].Volume);
        Assert.False(snapshot.Sessions[0].IsMuted);
    }
}
