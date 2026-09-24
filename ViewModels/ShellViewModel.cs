using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IMediaDetectionService _mediaService;
    private readonly IDispatcherService _dispatcher;
    private readonly MediaUpdateQueue _mediaUpdates;

    [ObservableProperty] private NotchView _currentView = NotchView.Compact;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isNotchVisible = true;
    [ObservableProperty] private bool _isMusicCompactMode;

    public MediaViewModel Media { get; }
    public ProgressViewModel Progress { get; }
    public SecondaryViewModel Secondary { get; }
    public AudioMixerViewModel AudioMixer { get; }
    public SettingsViewModel Settings { get; }
    public TimerViewModel Timer { get; } = new();

    public Func<bool>? IsExpandedCheck { get => Progress.IsExpandedCheck; set => Progress.IsExpandedCheck = value; }
    public event EventHandler<MediaInfo>? MediaInfoUpdated;
    public event EventHandler<NotchSettings>? SettingsApplied { add => Settings.Applied += value; remove => Settings.Applied -= value; }
    public event EventHandler<MediaInfo>? NewTrackDetected { add => Media.NewTrackDetected += value; remove => Media.NewTrackDetected -= value; }
    public event EventHandler<bool>? PlayPauseToggled { add => Media.PlayPauseToggled += value; remove => Media.PlayPauseToggled -= value; }
    public event EventHandler? SessionTransitioned { add => Progress.SessionTransitioned += value; remove => Progress.SessionTransitioned -= value; }
    public event EventHandler? NextTrackTriggered { add => Media.NextTrackTriggered += value; remove => Media.NextTrackTriggered -= value; }
    public event EventHandler? PreviousTrackTriggered { add => Media.PreviousTrackTriggered += value; remove => Media.PreviousTrackTriggered -= value; }

    private readonly NotchTransitionCoordinator? _coordinator;

    public ShellViewModel(IMediaDetectionService mediaService, ISettingsService settingsService,
        IVolumeService volumeService, IBatteryService batteryService, IDispatcherService dispatcher,
        NotchTransitionCoordinator? coordinator = null)
    {
        _mediaService = mediaService;
        _dispatcher = dispatcher;
        _coordinator = coordinator;
        Progress = new(mediaService);
        Media = new(mediaService, Progress.SeekRelative);
        Secondary = new(batteryService);
        AudioMixer = new(volumeService);
        Settings = new(settingsService);
        _mediaUpdates = new(dispatcher.BeginInvokeBackground, ApplyMediaUpdate);
        _mediaService.MediaChanged += OnMediaChanged;

        if (_coordinator != null)
        {
            _coordinator.StateChanged += (_, snapshot) => _dispatcher.BeginInvoke(() =>
            {
                if (snapshot != _coordinator.Snapshot) return;
                CurrentView = snapshot.CurrentView;
                IsExpanded = snapshot.CurrentView != NotchView.Compact;
            });
        }
    }

    public void Initialize()
    {
        _mediaService.Start();
        Secondary.UpdateBattery();
        Secondary.UpdateCalendar();
    }

    private void OnMediaChanged(object? sender, MediaInfo info) => _mediaUpdates.Enqueue(info);

    private void ApplyMediaUpdate(MediaInfo info)
    {
        if (!_mediaService.AcceptsMediaUpdate(info)) return;
        if (info.IsThumbnailOnlyUpdate)
        {
            var current = Media.CurrentInfo;
            if (current == null || !MediaUpdateQueue.SameTrack(current, info)) return;
            // Artwork completion can arrive after play/pause or a seek. Preserve
            // the latest playback snapshot rather than restoring its old timeline.
            var merged = current.Clone();
            merged.IsThumbnailOnlyUpdate = true;
            if (info.Thumbnail != null) merged.Thumbnail = info.Thumbnail;
            if (!string.IsNullOrEmpty(info.YouTubeVideoId)) merged.YouTubeVideoId = info.YouTubeVideoId;
            info = merged;
        }
        Media.Update(info);
        Progress.Update(info);
        IsMusicCompactMode = info.IsAnyMediaPlaying && !string.IsNullOrEmpty(info.CurrentTrack) &&
            !(info.Platform == MediaPlatform.Browser && string.IsNullOrEmpty(info.CurrentTrack));
        MediaInfoUpdated?.Invoke(this, info);
    }

    [RelayCommand]
    private void OpenMedia()
    {
        if (_coordinator != null) _coordinator.RequestView(NotchView.Media, "CommandOpenMedia");
        else SetView(NotchView.Media);
    }

    [RelayCommand]
    private void OpenTimer()
    {
        if (_coordinator != null) _coordinator.RequestView(NotchView.Timer, "CommandOpenTimer");
        else SetView(NotchView.Timer);
    }

    [RelayCommand]
    private void OpenSecondary()
    {
        if (_coordinator != null) _coordinator.RequestView(NotchView.Secondary, "CommandOpenSecondary");
        else SetView(NotchView.Secondary);
    }

    [RelayCommand]
    private void OpenAudioMixer()
    {
        if (_coordinator != null) _coordinator.RequestView(NotchView.AudioMixer, "CommandOpenAudioMixer");
        else SetView(NotchView.AudioMixer);
    }

    [RelayCommand]
    private void Collapse()
    {
        if (_coordinator != null) _coordinator.RequestCollapse("CommandCollapse");
        else SetView(NotchView.Compact);
    }

    [RelayCommand] private void ToggleNotch() => IsNotchVisible = !IsNotchVisible;

    public void SetView(NotchView view)
    {
        CurrentView = view;
        IsExpanded = view != NotchView.Compact;
    }

    // ponytail: compatibility ends when remaining code-behind reads child VMs directly.
    public bool IsPlaying => Media.IsPlaying;
    public double ProgressRatio => Progress.Position;
    public string CurrentTimeText => Progress.CurrentTimeText;
    public string RemainingTimeText => Progress.RemainingTimeText;
    public float CurrentVolume => AudioMixer.CurrentVolume;
    public string VolumeIconText => AudioMixer.IconText;
    public IAsyncRelayCommand PlayPauseCommand => Media.PlayPauseCommand;
    public IAsyncRelayCommand NextTrackCommand => Media.NextTrackCommand;
    public IAsyncRelayCommand PreviousTrackCommand => Media.PreviousTrackCommand;
    public void RenderProgressBar() => Progress.Render();
    public void StartDraggingProgress() => Progress.StartDragging();
    public void StopDraggingProgress() => Progress.StopDragging();
    public void UpdateDragPosition(double ratio) => Progress.UpdateDragPosition(ratio);
    public TimeSpan GetDragSeekPosition(double ratio) => Progress.GetDragSeekPosition(ratio);
    public Task SeekToPosition(TimeSpan position) => Progress.SeekToPosition(position);
    public Task SeekRelative(double seconds) => Progress.SeekRelative(seconds);
    public void SetVolumeFromRatio(float ratio) => AudioMixer.SetVolume(ratio);
    public void UpdateVolumeState(float volume, bool muted) => AudioMixer.UpdateState(volume, muted);
    public void UpdateBatteryInfo(BatteryInfo battery) => Secondary.UpdateBattery(battery);
    public void UpdateCalendarInfo() => Secondary.UpdateCalendar();

    public void Dispose()
    {
        _mediaService.MediaChanged -= OnMediaChanged;
        _mediaUpdates.Dispose();
    }
}
