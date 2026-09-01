using System.Windows.Media.Imaging;
using VNotch.Services;

namespace VNotch.Models;

public class MediaInfo
{
    public bool IsSpotifyRunning { get; set; }
    public bool IsSpotifyPlaying { get; set; }
    public bool IsYouTubeRunning { get; set; }
    public bool IsSoundCloudRunning { get; set; }
    public bool IsFacebookRunning { get; set; }
    public bool IsTikTokRunning { get; set; }
    public bool IsInstagramRunning { get; set; }
    public bool IsTwitterRunning { get; set; }
    public bool IsAppleMusicRunning { get; set; }
    public bool IsTwitchRunning { get; set; }
    public bool IsDiscordRunning { get; set; }
    public bool IsNetflixRunning { get; set; }
    public bool IsTidalRunning { get; set; }
    public bool IsDeezerRunning { get; set; }
    public bool IsBandcampRunning { get; set; }
    public bool IsBilibiliRunning { get; set; }
    public bool IsVimeoRunning { get; set; }

    public bool IsAnyMediaPlaying { get; set; }
    public bool IsPlaying { get; set; }
    public double PlaybackRate { get; set; } = 1.0;

    public string CurrentTrack { get; set; } = "";
    public string CurrentArtist { get; set; } = "";
    public string YouTubeTitle { get; set; } = "";
    public string MediaSource { get; set; } = "";
    public string SourceAppId { get; set; } = "";
    public string SessionInstanceKey { get; set; } = "";
    public string? YouTubeVideoId { get; set; }
    public BitmapImage? Thumbnail { get; set; }
    public bool HasThumbnail => Thumbnail != null;

    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.Now;
    public bool IsIndeterminate { get; set; }
    public bool IsSeekEnabled { get; set; }
    public bool IsThrottled { get; set; }
    public bool IsThumbnailOnlyUpdate { get; set; }

    public double Progress => Duration.TotalSeconds > 0 ? Position.TotalSeconds / Duration.TotalSeconds : 0;
    public bool HasTimeline => Duration.TotalSeconds > 0 && !IsIndeterminate;

    public MediaPlatform Platform => MediaPlatformExtensions.ParsePlatform(MediaSource);

    public bool IsVideoSource => Platform is MediaPlatform.YouTube or MediaPlatform.Browser
        or MediaPlatform.Facebook or MediaPlatform.TikTok or MediaPlatform.Instagram or MediaPlatform.Twitter
        or MediaPlatform.Twitch or MediaPlatform.Discord or MediaPlatform.Netflix or MediaPlatform.Bilibili or MediaPlatform.Vimeo;

    public MediaInfo Clone() => (MediaInfo)MemberwiseClone();

    public string GetSignature() => $"{CurrentTrack}|{CurrentArtist}|{MediaSource}";
}
