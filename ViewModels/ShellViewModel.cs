using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IMediaDetectionService _mediaService;
    private readonly IDispatcherService _dispatcher;
    private readonly MediaUpdateQueue _mediaUpdates;

    [ObservableProperty] private NotchView _currentView = NotchView.Compact;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isNotchVisible = true;

    public MediaViewModel Media { get; }
    public ProgressViewModel Progress { get; }
    public SecondaryViewModel Secondary { get; }
    public AudioMixerViewModel AudioMixer { get; }
    public TimerViewModel Timer { get; } = new();

    public event EventHandler<MediaInfo>? MediaInfoUpdated;

    private readonly INotchNavigation? _coordinator;

    public ShellViewModel(IMediaDetectionService mediaService,
        IVolumeService volumeService, IBatteryService batteryService, IDispatcherService dispatcher,
        INotchNavigation? coordinator = null)
    {
        _mediaService = mediaService;
        _dispatcher = dispatcher;
        _coordinator = coordinator;
        Progress = new(mediaService);
        Media = new(mediaService);
        Secondary = new(batteryService);
        AudioMixer = new(volumeService);
        _mediaUpdates = new(dispatcher.BeginInvokeBackground, ApplyMediaUpdate);
        _mediaService.MediaChanged += OnMediaChanged;

        if (_coordinator != null)
        {
            _coordinator.StateChanged += OnCoordinatorStateChanged;
        }
    }

    private void OnCoordinatorStateChanged(object? sender, NotchTransitionSnapshot snapshot)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_coordinator == null || snapshot != _coordinator.Snapshot) return;
            CurrentView = snapshot.CurrentView;
            IsExpanded = snapshot.CurrentView != NotchView.Compact;
        });
    }

    public void Initialize()
    {
        _mediaService.Start();
        Secondary.UpdateBattery();
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
        MediaInfoUpdated?.Invoke(this, info);
    }

    [RelayCommand]
    private void OpenTimer()
    {
        if (_coordinator != null) _coordinator.RequestView(NotchView.Timer, "CommandOpenTimer");
        else SetView(NotchView.Timer);
    }

    [RelayCommand]
    private void Collapse()
    {
        if (_coordinator != null) _coordinator.RequestCollapse("CommandCollapse");
        else SetView(NotchView.Compact);
    }

    public void SetView(NotchView view)
    {
        CurrentView = view;
        IsExpanded = view != NotchView.Compact;
    }

    public void Dispose()
    {
        if (_coordinator != null)
        {
            _coordinator.StateChanged -= OnCoordinatorStateChanged;
        }
        _mediaService.MediaChanged -= OnMediaChanged;
        _mediaUpdates.Dispose();
    }
}
