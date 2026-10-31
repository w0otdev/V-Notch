using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.ViewModels;

public partial class MediaViewModel : ObservableObject
{
    private readonly IMediaDetectionService _service;

    private DateTime _lastAction = DateTime.MinValue;
    private string _lastAnimatedSignature = "";

    [ObservableProperty]
    private bool _isPlaying = true;

    [ObservableProperty]
    private string _title = Loc.Get("media.noMedia");

    [ObservableProperty]
    private string _artist = Loc.Get("media.unknownArtist");

    [ObservableProperty]
    private string _sourceName = Loc.Get("media.nowPlaying");

    [ObservableProperty]
    private string _sourceIcon = "";

    [ObservableProperty]
    private BitmapSource? _thumbnail;

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
        _ = seekRelative;
    }

    public void Update(MediaInfo info)
    {
        CurrentInfo = info;
        IsPictureInPicture = info.IsPictureInPicture;
        bool hasTrack = !string.IsNullOrEmpty(info.CurrentTrack);
        SourceIcon = hasTrack ? info.MediaSource ?? "" : "";
        if (hasTrack)
        {
            SourceName = !string.IsNullOrEmpty(info.MediaSource) ? info.MediaSource : Loc.Get("media.nowPlaying");
            Title = info.CurrentTrack;
            bool hasArtist = !string.IsNullOrEmpty(info.CurrentArtist) &&
                MediaPlatformExtensions.ParsePlatform(info.CurrentArtist) is not (MediaPlatform.YouTube or MediaPlatform.Browser or MediaPlatform.Spotify);
            string fallbackArtist = !string.IsNullOrEmpty(info.MediaSource) ? info.MediaSource : Loc.Get("media.unknownArtist");
            Artist = hasArtist ? info.CurrentArtist : fallbackArtist;
        }
        else
        {
            Title = Loc.Get("media.noMedia");
            Artist = Loc.Get("media.unknownArtist");
            SourceName = Loc.Get("media.nowPlaying");
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

        if ((DateTime.UtcNow - _lastAction).TotalMilliseconds > 1200 && IsPlaying != info.IsPlaying)
        {
            IsPlaying = info.IsPlaying;
        }
    }

    [RelayCommand]
    private async Task PlayPause()
    {
        if (!BeginAction()) return;
        IsPlaying = !IsPlaying;
        PlayPauseToggled?.Invoke(this, IsPlaying);
        try
        {
            await _service.PlayPauseAsync();
        }
        finally
        {
            _lastAction = DateTime.UtcNow;
        }
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
        if ((DateTime.UtcNow - _lastAction).TotalMilliseconds < 200) return false;
        _lastAction = DateTime.UtcNow;
        return true;
    }
}
