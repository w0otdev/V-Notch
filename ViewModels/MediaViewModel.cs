using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class MediaViewModel : ObservableObject
{
    private readonly IMediaDetectionService _service;
    private readonly Func<double, Task> _seekRelative;
    private DateTime _lastAction = DateTime.MinValue;
    private string _lastAnimatedSignature = "";

    [ObservableProperty]
    private bool _isPlaying = true;

    [ObservableProperty]
    private string _title = "No media playing";

    [ObservableProperty]
    private string _artist = "Artist name";

    [ObservableProperty]
    private string _sourceName = "Now Playing";

    [ObservableProperty]
    private string _sourceIcon = "";

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    [ObservableProperty]
    private bool _hasThumbnail;

    [ObservableProperty]
    private MediaInfo? _currentInfo;

    [ObservableProperty]
    private bool _isPictureInPicture;

    public event EventHandler<MediaInfo>? NewTrackDetected;
    public event EventHandler<bool>? PlayPauseToggled;
    public event EventHandler? NextTrackTriggered;
    public event EventHandler? PreviousTrackTriggered;

    public MediaViewModel(IMediaDetectionService service, Func<double, Task> seekRelative)
    {
        _service = service;
        _seekRelative = seekRelative;
    }

    public void Update(MediaInfo info)
    {
        CurrentInfo = info;
        IsPictureInPicture = info.IsPictureInPicture;
        bool hasTrack = !string.IsNullOrEmpty(info.CurrentTrack);
        SourceIcon = hasTrack ? info.MediaSource ?? "" : "";
        if (hasTrack)
        {
            SourceName = info.MediaSource ?? "Now Playing";
            Title = info.CurrentTrack;
            Artist = !string.IsNullOrEmpty(info.CurrentArtist) &&
                MediaPlatformExtensions.ParsePlatform(info.CurrentArtist) is not (MediaPlatform.YouTube or MediaPlatform.Browser or MediaPlatform.Spotify)
                ? info.CurrentArtist : !string.IsNullOrEmpty(info.MediaSource) ? info.MediaSource : "Unknown Artist";
        }
        else
        {
            Title = "No media playing";
            Artist = "Artist name";
            SourceName = "Now Playing";
        }

        string signature = info.GetSignature();
        if (hasTrack && info.HasThumbnail && info.Thumbnail != null)
        {
            Thumbnail = info.Thumbnail;
            HasThumbnail = true;
            if (signature != _lastAnimatedSignature)
            {
                _lastAnimatedSignature = signature;
                NewTrackDetected?.Invoke(this, info);
            }
        }
        else if (!hasTrack)
        {
            _lastAnimatedSignature = "";
            Thumbnail = null;
            HasThumbnail = false;
        }

        if ((DateTime.Now - _lastAction).TotalMilliseconds > 500 && IsPlaying != info.IsPlaying)
        {
            IsPlaying = info.IsPlaying;
            PlayPauseToggled?.Invoke(this, IsPlaying);
        }
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (!BeginAction()) return;
        IsPlaying = !IsPlaying;
        PlayPauseToggled?.Invoke(this, IsPlaying);
        await _service.PlayPauseAsync();
    }

    [RelayCommand]
    private async Task NextTrack()
    {
        if (!BeginAction()) return;
        NextTrackTriggered?.Invoke(this, EventArgs.Empty);
        await _service.NextTrackAsync();
    }

    [RelayCommand]
    private async Task PreviousTrack()
    {
        if (!BeginAction()) return;
        PreviousTrackTriggered?.Invoke(this, EventArgs.Empty);
        await _service.PreviousTrackAsync();
    }

    private bool BeginAction()
    {
        if ((DateTime.Now - _lastAction).TotalMilliseconds < 200) return false;
        _lastAction = DateTime.Now;
        return true;
    }
}
