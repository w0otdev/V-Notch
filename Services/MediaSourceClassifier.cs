using VNotch.Models;

namespace VNotch.Services;

internal static class MediaSourceClassifier
{
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

        bool isYouTube = artist.Contains("youtube") ||
                         title.Contains("youtube") ||
                         title.EndsWith("- youtube") ||
                         title.EndsWith("– youtube") ||
                         album.Contains("youtube");

        if (isYouTube)
        {
            info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
            info.IsYouTubeRunning = true;
        }
        else if (artist.Contains("twitch") || title.Contains("twitch") || title.EndsWith("- twitch") || title.EndsWith("– twitch") || album.Contains("twitch"))
        {
            info.MediaSource = MediaPlatform.Twitch.ToDisplayString();
            info.IsTwitchRunning = true;
        }
        else if (artist.Contains("discord") || title.Contains("discord") || album.Contains("discord"))
        {
            info.MediaSource = MediaPlatform.Discord.ToDisplayString();
            info.IsDiscordRunning = true;
        }
        else if (artist.Contains("apple music") || title.Contains("apple music") || album.Contains("apple music") || album.Contains("music.apple.com"))
        {
            info.MediaSource = MediaPlatform.AppleMusic.ToDisplayString();
            info.IsAppleMusicRunning = true;
        }
        else if (artist.Contains("soundcloud") || title.Contains("soundcloud") || album.Contains("soundcloud"))
        {
            info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
            info.IsSoundCloudRunning = true;
        }
        else if (artist.Contains("tidal") || title.Contains("tidal") || album.Contains("tidal"))
        {
            info.MediaSource = MediaPlatform.Tidal.ToDisplayString();
            info.IsTidalRunning = true;
        }
        else if (artist.Contains("deezer") || title.Contains("deezer") || album.Contains("deezer"))
        {
            info.MediaSource = MediaPlatform.Deezer.ToDisplayString();
            info.IsDeezerRunning = true;
        }
        else if (artist.Contains("bandcamp") || title.Contains("bandcamp") || album.Contains("bandcamp"))
        {
            info.MediaSource = MediaPlatform.Bandcamp.ToDisplayString();
            info.IsBandcampRunning = true;
        }
        else if (artist.Contains("netflix") || title.Contains("netflix") || album.Contains("netflix"))
        {
            info.MediaSource = MediaPlatform.Netflix.ToDisplayString();
            info.IsNetflixRunning = true;
        }
        else if (artist.Contains("bilibili") || title.Contains("bilibili") || artist.Contains("哔哩哔哩") || title.Contains("哔哩哔哩"))
        {
            info.MediaSource = MediaPlatform.Bilibili.ToDisplayString();
            info.IsBilibiliRunning = true;
        }
        else if (artist.Contains("vimeo") || title.Contains("vimeo") || album.Contains("vimeo"))
        {
            info.MediaSource = MediaPlatform.Vimeo.ToDisplayString();
            info.IsVimeoRunning = true;
        }
    }

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

            if (winTitleLower.Contains("youtube") && !winTitleLower.StartsWith("youtube -") && winTitleLower != "youtube")
            {
                info.MediaSource = MediaPlatform.YouTube.ToDisplayString();
                info.IsYouTubeRunning = true;
                string extractedYouTubeTitle = PlatformDetector.ExtractTitleFromWindow(title, "YouTube");
                if (!string.IsNullOrWhiteSpace(extractedYouTubeTitle) &&
                    extractedYouTubeTitle.Length > info.CurrentTrack.Length &&
                    PlatformDetector.NormalizeForLooseMatch(extractedYouTubeTitle).Contains(PlatformDetector.NormalizeForLooseMatch(info.CurrentTrack), StringComparison.Ordinal))
                {
                    info.CurrentTrack = extractedYouTubeTitle;
                }
                break;
            }
            else if (winTitleLower.Contains("twitch") && !winTitleLower.StartsWith("twitch -") && winTitleLower != "twitch")
            {
                info.MediaSource = MediaPlatform.Twitch.ToDisplayString();
                info.IsTwitchRunning = true;
                string extractedTwitchTitle = PlatformDetector.ExtractTitleFromWindow(title, "Twitch");
                if (!string.IsNullOrWhiteSpace(extractedTwitchTitle) &&
                    (string.IsNullOrEmpty(info.CurrentTrack) ||
                     (extractedTwitchTitle.Length > info.CurrentTrack.Length &&
                      PlatformDetector.NormalizeForLooseMatch(extractedTwitchTitle).Contains(PlatformDetector.NormalizeForLooseMatch(info.CurrentTrack), StringComparison.Ordinal))))
                {
                    info.CurrentTrack = extractedTwitchTitle;
                }
                break;
            }
            else if ((winTitleLower.Contains("discord") || winTitleLower.Contains("vesktop")) &&
                     !winTitleLower.StartsWith("discord -") && winTitleLower != "discord" && winTitleLower != "vesktop")
            {
                info.MediaSource = MediaPlatform.Discord.ToDisplayString();
                info.IsDiscordRunning = true;
                string extractedDiscordTitle = PlatformDetector.ExtractTitleFromWindow(title, "Discord");
                if (!string.IsNullOrWhiteSpace(extractedDiscordTitle) &&
                    (string.IsNullOrEmpty(info.CurrentTrack) ||
                     (extractedDiscordTitle.Length > info.CurrentTrack.Length &&
                      PlatformDetector.NormalizeForLooseMatch(extractedDiscordTitle).Contains(PlatformDetector.NormalizeForLooseMatch(info.CurrentTrack), StringComparison.Ordinal))))
                {
                    info.CurrentTrack = extractedDiscordTitle;
                }
                break;
            }
            else if (winTitleLower.Contains("soundcloud"))
            {
                info.MediaSource = MediaPlatform.SoundCloud.ToDisplayString();
                info.IsSoundCloudRunning = true;
                break;
            }
            else if (winTitleLower.Contains("apple music") || winTitleLower.Contains("music.apple.com") ||
                     (winTitleLower.Contains("apple") && winTitleLower.Contains("music")))
            {
                info.MediaSource = MediaPlatform.AppleMusic.ToDisplayString();
                info.IsAppleMusicRunning = true;
                break;
            }
            else if (winTitleLower.Contains("tidal") && (winTitleLower.Contains("listen.tidal.com") || winTitleLower.Contains(" - tidal") || winTitleLower.Contains(" – tidal")))
            {
                info.MediaSource = MediaPlatform.Tidal.ToDisplayString();
                info.IsTidalRunning = true;
                break;
            }
            else if (winTitleLower.Contains("deezer"))
            {
                info.MediaSource = MediaPlatform.Deezer.ToDisplayString();
                info.IsDeezerRunning = true;
                break;
            }
            else if (winTitleLower.Contains("bandcamp"))
            {
                info.MediaSource = MediaPlatform.Bandcamp.ToDisplayString();
                info.IsBandcampRunning = true;
                break;
            }
            else if (winTitleLower.Contains("netflix"))
            {
                info.MediaSource = MediaPlatform.Netflix.ToDisplayString();
                info.IsNetflixRunning = true;
                break;
            }
            else if (winTitleLower.Contains("bilibili") || winTitleLower.Contains("哔哩哔哩"))
            {
                info.MediaSource = MediaPlatform.Bilibili.ToDisplayString();
                info.IsBilibiliRunning = true;
                break;
            }
            else if (winTitleLower.Contains("vimeo"))
            {
                info.MediaSource = MediaPlatform.Vimeo.ToDisplayString();
                info.IsVimeoRunning = true;
                break;
            }
            else if (winTitleLower.Contains("facebook") && (winTitleLower.Contains("watch") || winTitleLower.Contains("video")))
            {
                info.MediaSource = MediaPlatform.Facebook.ToDisplayString();
                info.IsFacebookRunning = true;
                break;
            }
            else if (winTitleLower.Contains("tiktok") && winTitleLower.Contains(" | "))
            {
                info.MediaSource = MediaPlatform.TikTok.ToDisplayString();
                info.IsTikTokRunning = true;
                break;
            }
            else if (winTitleLower.Contains("instagram") && (winTitleLower.Contains("reel") || winTitleLower.Contains("video")))
            {
                info.MediaSource = MediaPlatform.Instagram.ToDisplayString();
                info.IsInstagramRunning = true;
                break;
            }
            else if ((winTitleLower.Contains("twitter") || winTitleLower.Contains(" / x")) && (winTitleLower.Contains("video") || winTitleLower.Contains("watch")))
            {
                info.MediaSource = MediaPlatform.Twitter.ToDisplayString();
                info.IsTwitterRunning = true;
                break;
            }
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
                           lowerTitle == "discord" ||
                           lowerTitle == "vesktop" ||
                           lowerTitle == "twitch" ||
                           lowerTitle == "netflix" ||
                           lowerTitle == "tidal" ||
                           lowerTitle == "deezer" ||
                           lowerTitle == "bandcamp" ||
                           (lowerTitle == "youtube" && (string.IsNullOrEmpty(sessionArtist) || lowerArtist == "youtube"));

        if (!isJunkTitle) return false;

        if (info.MediaSource == MediaPlatform.YouTube.ToDisplayString())
        {
            info.CurrentTrack = "";
            info.CurrentArtist = MediaPlatform.YouTube.ToDisplayString();
        }

        return true;
    }
}
