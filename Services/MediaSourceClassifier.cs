using System;
using System.Collections.Generic;
using VNotch.Models;

namespace VNotch.Services;

internal static class MediaSourceClassifier
{
    private const string YouTubeToken = "youtube";
    private const string TwitchToken = "twitch";
    private const string DiscordToken = "discord";
    private const string AppleMusicToken = "apple music";
    private const string SoundCloudToken = "soundcloud";
    private const string TidalToken = "tidal";
    private const string DeezerToken = "deezer";
    private const string BandcampToken = "bandcamp";
    private const string NetflixToken = "netflix";
    private const string VimeoToken = "vimeo";

    private static readonly (string Token, MediaPlatform Platform)[] SimpleMetadataTokens =
    {
        (SoundCloudToken, MediaPlatform.SoundCloud),
        (TidalToken, MediaPlatform.Tidal),
        (DeezerToken, MediaPlatform.Deezer),
        (BandcampToken, MediaPlatform.Bandcamp),
        (NetflixToken, MediaPlatform.Netflix),
        (VimeoToken, MediaPlatform.Vimeo),
    };

    private static readonly (string Token, MediaPlatform Platform)[] SimpleWindowTokens =
    {
        (SoundCloudToken, MediaPlatform.SoundCloud),
        (DeezerToken, MediaPlatform.Deezer),
        (BandcampToken, MediaPlatform.Bandcamp),
        (NetflixToken, MediaPlatform.Netflix),
        (VimeoToken, MediaPlatform.Vimeo),
    };

    public static void ApplyFromAppId(MediaInfo info, string sessionSourceApp)
    {
        if (string.IsNullOrEmpty(sessionSourceApp)) return;

        if (sessionSourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.Spotify.ToDisplayString();
            info.IsSpotifyPlaying = true;
            info.IsSpotifyRunning = true;
        }
        else if (sessionSourceApp.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
            info.IsYouTubeRunning = true;
        }
        else if (sessionSourceApp.Contains("Discord", StringComparison.OrdinalIgnoreCase) ||
                 sessionSourceApp.Contains("Vesktop", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.Discord.ToDisplayString();
            info.IsDiscordRunning = true;
        }
        else if (sessionSourceApp.Contains("Twitch", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.Twitch.ToDisplayString();
            info.IsTwitchRunning = true;
        }
        else if (sessionSourceApp.Contains("TIDAL", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.Tidal.ToDisplayString();
            info.IsTidalRunning = true;
        }
        else if (sessionSourceApp.Contains("Deezer", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.Deezer.ToDisplayString();
            info.IsDeezerRunning = true;
        }
        else if (PlatformDetector.IsBrowserApp(sessionSourceApp))
        {
            info.MediaSource = MediaPlatform.Browser.ToDisplayString();
        }
        else if (sessionSourceApp.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
                 sessionSourceApp.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
                 sessionSourceApp.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase))
        {
            info.MediaSource = MediaPlatform.AppleMusic.ToDisplayString();
            info.IsAppleMusicRunning = true;
        }
        else
        {
            info.MediaSource = MediaPlatform.Browser.ToDisplayString();
        }
    }

    public static void RefineFromMetadata(MediaInfo info, string lowerTitle, string lowerArtist, string lowerAlbum)
    {
        if (info.MediaSource != MediaPlatform.Browser.ToDisplayString() && !string.IsNullOrEmpty(info.MediaSource)) return;

        string title = (lowerTitle ?? "").ToLowerInvariant();
        string artist = (lowerArtist ?? "").ToLowerInvariant();
        string album = (lowerAlbum ?? "").ToLowerInvariant();

        var detected = DetectPlatformFromMetadata(title, artist, album);
        if (detected != MediaPlatform.Unknown)
        {
            SetPlatformRunning(info, detected);
        }
    }

    private static MediaPlatform DetectPlatformFromMetadata(string title, string artist, string album)
    {
        if (MatchesMetadata(title, artist, album, YouTubeToken) || title.EndsWith("- youtube") || title.EndsWith("– youtube"))
            return MediaPlatform.YouTube;

        if (MatchesMetadata(title, artist, album, TwitchToken) || title.EndsWith("- twitch") || title.EndsWith("– twitch"))
            return MediaPlatform.Twitch;

        if (MatchesMetadata(title, artist, album, DiscordToken))
            return MediaPlatform.Discord;

        if (MatchesMetadata(title, artist, album, AppleMusicToken) || album.Contains("music.apple.com"))
            return MediaPlatform.AppleMusic;

        if (artist.Contains("bilibili") || title.Contains("bilibili") || artist.Contains("哔哩哔哩") || title.Contains("哔哩哔哩"))
            return MediaPlatform.Bilibili;

        foreach (var (token, platform) in SimpleMetadataTokens)
        {
            if (MatchesMetadata(title, artist, album, token))
                return platform;
        }

        return MediaPlatform.Unknown;
    }

    private static bool MatchesMetadata(string title, string artist, string album, string token)
        => artist.Contains(token) || title.Contains(token) || album.Contains(token);

    public static void DetectFromWindowTitles(
        MediaInfo info,
        IEnumerable<string> windowTitles,
        string trackTitleLower,
        string trackTitleNormalized,
        bool hasTrack)
    {
        foreach (var title in windowTitles)
        {
            if (info.Platform == MediaPlatform.YouTube)
            {
                break;
            }

            var winTitleLower = title.ToLowerInvariant();
            bool trackMatch = winTitleLower.Contains(trackTitleLower);

            if (!trackMatch && !string.IsNullOrEmpty(trackTitleNormalized))
            {
                var winTitleNormalized = PlatformDetector.NormalizeForLooseMatch(winTitleLower);
                trackMatch = winTitleNormalized.Contains(trackTitleNormalized, StringComparison.Ordinal);
            }

            if (hasTrack && !trackMatch)
            {
                continue;
            }

            if (TryDetectPlatformFromWindowTitle(info, title, winTitleLower))
            {
                break;
            }
        }
    }

    private static bool TryDetectPlatformFromWindowTitle(MediaInfo info, string title, string winTitleLower)
    {
        if (winTitleLower.Contains(YouTubeToken) && !winTitleLower.StartsWith("youtube -") && winTitleLower != YouTubeToken)
        {
            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
            info.IsYouTubeRunning = true;
            UpdateTrackTitleIfBetter(info, title, "YouTube");
            return true;
        }

        if (winTitleLower.Contains(TwitchToken) && !winTitleLower.StartsWith("twitch -") && winTitleLower != TwitchToken)
        {
            info.MediaSource = MediaPlatform.Twitch.ToDisplayString();
            info.IsTwitchRunning = true;
            UpdateTrackTitleIfBetter(info, title, "Twitch");
            return true;
        }

        if ((winTitleLower.Contains(DiscordToken) || winTitleLower.Contains("vesktop")) &&
            !winTitleLower.StartsWith("discord -") && winTitleLower != DiscordToken && winTitleLower != "vesktop")
        {
            info.MediaSource = MediaPlatform.Discord.ToDisplayString();
            info.IsDiscordRunning = true;
            UpdateTrackTitleIfBetter(info, title, "Discord");
            return true;
        }

        return TryDetectSecondaryPlatformsFromWindowTitle(info, winTitleLower);
    }

    private static void UpdateTrackTitleIfBetter(MediaInfo info, string title, string platformKey)
    {
        string extracted = PlatformDetector.ExtractTitleFromWindow(title, platformKey);
        if (!string.IsNullOrWhiteSpace(extracted) &&
            (string.IsNullOrEmpty(info.CurrentTrack) ||
             (extracted.Length > info.CurrentTrack.Length &&
              PlatformDetector.NormalizeForLooseMatch(extracted).Contains(PlatformDetector.NormalizeForLooseMatch(info.CurrentTrack), StringComparison.Ordinal))))
        {
            info.CurrentTrack = extracted;
        }
    }

    private static bool TryDetectSecondaryPlatformsFromWindowTitle(MediaInfo info, string winTitleLower)
    {
        var platform = DetectMusicPlatformFromWindowTitle(winTitleLower);
        if (platform == MediaPlatform.Unknown)
        {
            platform = DetectSocialVideoPlatformFromWindowTitle(winTitleLower);
        }

        if (platform != MediaPlatform.Unknown)
        {
            SetPlatformRunning(info, platform);
            return true;
        }

        return false;
    }

    private static MediaPlatform DetectMusicPlatformFromWindowTitle(string winTitleLower)
    {
        foreach (var (token, platform) in SimpleWindowTokens)
        {
            if (winTitleLower.Contains(token))
                return platform;
        }

        if (winTitleLower.Contains(AppleMusicToken) || winTitleLower.Contains("music.apple.com") ||
            (winTitleLower.Contains("apple") && winTitleLower.Contains("music")))
        {
            return MediaPlatform.AppleMusic;
        }

        if (winTitleLower.Contains(TidalToken) &&
            (winTitleLower.Contains("listen.tidal.com") || winTitleLower.Contains(" - tidal") || winTitleLower.Contains(" – tidal")))
        {
            return MediaPlatform.Tidal;
        }

        if (winTitleLower.Contains("bilibili") || winTitleLower.Contains("哔哩哔哩"))
        {
            return MediaPlatform.Bilibili;
        }

        return MediaPlatform.Unknown;
    }

    private static MediaPlatform DetectSocialVideoPlatformFromWindowTitle(string winTitleLower)
    {
        if (winTitleLower.Contains("facebook") && (winTitleLower.Contains("watch") || winTitleLower.Contains("video")))
            return MediaPlatform.Facebook;

        if (winTitleLower.Contains("tiktok") && winTitleLower.Contains(" | "))
            return MediaPlatform.TikTok;

        if (winTitleLower.Contains("instagram") && (winTitleLower.Contains("reel") || winTitleLower.Contains("video")))
            return MediaPlatform.Instagram;

        if ((winTitleLower.Contains("twitter") || winTitleLower.Contains(" / x")) &&
            (winTitleLower.Contains("video") || winTitleLower.Contains("watch")))
            return MediaPlatform.Twitter;

        return MediaPlatform.Unknown;
    }

    private static void SetPlatformRunning(MediaInfo info, MediaPlatform platform)
    {
        info.MediaSource = platform.ToDisplayString();
        switch (platform)
        {
            case MediaPlatform.Spotify: info.IsSpotifyRunning = true; break;
            case MediaPlatform.YouTube: info.IsYouTubeRunning = true; break;
            case MediaPlatform.Discord: info.IsDiscordRunning = true; break;
            case MediaPlatform.Twitch: info.IsTwitchRunning = true; break;
            case MediaPlatform.AppleMusic: info.IsAppleMusicRunning = true; break;
            case MediaPlatform.SoundCloud: info.IsSoundCloudRunning = true; break;
            case MediaPlatform.Tidal: info.IsTidalRunning = true; break;
            case MediaPlatform.Deezer: info.IsDeezerRunning = true; break;
            case MediaPlatform.Bandcamp: info.IsBandcampRunning = true; break;
            case MediaPlatform.Netflix: info.IsNetflixRunning = true; break;
            case MediaPlatform.Bilibili: info.IsBilibiliRunning = true; break;
            case MediaPlatform.Vimeo: info.IsVimeoRunning = true; break;
            case MediaPlatform.Facebook: info.IsFacebookRunning = true; break;
            case MediaPlatform.TikTok: info.IsTikTokRunning = true; break;
            case MediaPlatform.Instagram: info.IsInstagramRunning = true; break;
            case MediaPlatform.Twitter: info.IsTwitterRunning = true; break;
            default: break;
        }
    }

    public static bool TryHandleJunkTitle(MediaInfo info, string sessionTitle, string sessionArtist)
    {
        string lowerTitle = sessionTitle.ToLowerInvariant();
        string lowerArtist = sessionArtist.ToLowerInvariant();

        bool isJunkTitle = string.IsNullOrEmpty(sessionTitle) ||
                           lowerTitle == "spotify" ||
                           lowerTitle == "advertisement" ||
                           lowerTitle == "windows media player" ||
                           lowerTitle == "spotify free" ||
                           lowerTitle == "spotify premium" ||
                           lowerTitle == "chrome" ||
                           lowerTitle == "edge" ||
                           lowerTitle == "brave" ||
                           lowerTitle == "opera" ||
                           lowerTitle == "firefox" ||
                           lowerTitle == DiscordToken ||
                           lowerTitle == "vesktop" ||
                           lowerTitle == TwitchToken ||
                           lowerTitle == NetflixToken ||
                           lowerTitle == TidalToken ||
                           lowerTitle == DeezerToken ||
                           lowerTitle == BandcampToken ||
                           (lowerTitle == YouTubeToken && (string.IsNullOrEmpty(sessionArtist) || lowerArtist == YouTubeToken));

        if (!isJunkTitle) return false;

        if (info.MediaSource == MediaPlatform.YouTube.ToDisplayString())
        {
            info.CurrentTrack = "";
            info.CurrentArtist = MediaPlatform.YouTube.ToDisplayString();
        }

        return true;
    }
}
