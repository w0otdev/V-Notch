using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VNotch.Services;

public static class PlatformDetector
{

    private static readonly (string Pattern, MediaPlatform Platform)[] AppIdRules =
    {
        ("Spotify", MediaPlatform.Spotify),
        ("Discord", MediaPlatform.Discord),
        ("Vesktop", MediaPlatform.Discord),
        ("Twitch", MediaPlatform.Twitch),
        ("TIDAL", MediaPlatform.Tidal),
        ("Deezer", MediaPlatform.Deezer),
    };

    private static readonly (string Pattern, MediaPlatform Platform)[] BrowserAppIdPatterns =
    {
        ("Chrome", MediaPlatform.Browser),
        ("Edge", MediaPlatform.Browser),
        ("Firefox", MediaPlatform.Browser),
        ("MS-Edge", MediaPlatform.Browser),
        ("msedge", MediaPlatform.Browser),
        ("Opera", MediaPlatform.Browser),
        ("Brave", MediaPlatform.Browser),
        ("Vivaldi", MediaPlatform.Browser),
        ("Coccoc", MediaPlatform.Browser),
        ("Arc", MediaPlatform.Browser),
        ("Sidekick", MediaPlatform.Browser),
        ("Zen", MediaPlatform.Browser),
        ("Thorium", MediaPlatform.Browser),
        ("Waterfox", MediaPlatform.Browser),
        ("Floorp", MediaPlatform.Browser),
        ("Librewolf", MediaPlatform.Browser),
        ("Chromium", MediaPlatform.Browser),
        ("Whale", MediaPlatform.Browser),
        ("Yandex", MediaPlatform.Browser),
        ("Wavebox", MediaPlatform.Browser),
        ("Helium", MediaPlatform.Browser),
        ("Supermium", MediaPlatform.Browser),
        ("Browser", MediaPlatform.Browser),
    };

    private static readonly string[] TitleSeparators =
    {
        " - YouTube", " – YouTube", " - SoundCloud", " | Facebook",
        " - TikTok", " / X", " | TikTok", " • Instagram",
        " - Apple Music", " – Apple Music",
        " - Twitch", " – Twitch", " | Twitch", " • Twitch",
        " - Discord", " – Discord", " | Discord", " • Discord",
        " - TIDAL", " – TIDAL", " - Deezer", " – Deezer",
        " | Bandcamp", " - Netflix", " – Netflix",
        " _哔哩哔哩_bilibili", " - 哔哩哔哩", " - bilibili",
        " on Vimeo", " - Crunchyroll", " – Crunchyroll",
        " - Google Chrome", " - Microsoft\u200B Edge", " - Microsoft Edge",
        " - Mozilla Firefox", " - Opera", " - Brave", " - Vivaldi",
        " - Cốc Cốc", " - Arc", " - Sidekick", " - Zen Browser", " - Zen",
        " - Thorium", " - Waterfox", " - Floorp", " - Yandex",
        " - Browser", " – Current browser"
    };

    public static MediaPlatform DetectFromAppId(string? sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
            return MediaPlatform.Unknown;

        foreach (var (pattern, platform) in AppIdRules)
        {
            if (sourceAppId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return platform;
        }

        return MediaPlatform.Unknown;
    }
    public static bool IsBrowserApp(string? sourceAppId)
    {
        if (string.IsNullOrEmpty(sourceAppId))
            return false;

        foreach (var (pattern, _) in BrowserAppIdPatterns)
        {
            if (sourceAppId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
    public static MediaPlatform DetectFromWindowTitles(IEnumerable<string> windowTitles)
    {
        MediaPlatform fallback = MediaPlatform.Unknown;

        foreach (var title in windowTitles)
        {
            var lower = title.ToLowerInvariant();

            if (lower.Contains("youtube") && !lower.StartsWith("youtube -") && lower != "youtube")
                return MediaPlatform.YouTube;

            if (lower.Contains("twitch") && !lower.StartsWith("twitch -") && lower != "twitch")
                return MediaPlatform.Twitch;

            if ((lower.Contains("discord") || lower.Contains("vesktop")) && !lower.StartsWith("discord -") && lower != "discord" && lower != "vesktop" && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Discord;
            else if (lower.Contains("soundcloud") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.SoundCloud;
            else if ((lower.Contains("apple music") || lower.Contains("music.apple.com")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.AppleMusic;
            else if (lower.Contains("tidal") && (lower.Contains("listen.tidal.com") || lower.Contains(" - tidal") || lower.Contains(" – tidal")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Tidal;
            else if (lower.Contains("deezer") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Deezer;
            else if (lower.Contains("bandcamp") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Bandcamp;
            else if (lower.Contains("netflix") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Netflix;
            else if ((lower.Contains("bilibili") || lower.Contains("哔哩哔哩")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Bilibili;
            else if (lower.Contains("vimeo") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Vimeo;
            else if (lower.Contains("facebook") && (lower.Contains("watch") || lower.Contains("video")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Facebook;
            else if (lower.Contains("tiktok") && lower.Contains(" | ") && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.TikTok;
            else if (lower.Contains("instagram") && (lower.Contains("reel") || lower.Contains("video")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Instagram;
            else if ((lower.Contains("twitter") || lower.Contains(" / x")) && (lower.Contains("video") || lower.Contains("watch")) && fallback == MediaPlatform.Unknown)
                fallback = MediaPlatform.Twitter;
        }

        return fallback;
    }
    public static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        var platform = DetectFromWindowTitles(windowTitles);
        return platform.ToDisplayString();
    }
    public static bool HasReliableWindowMatch(IEnumerable<string> windowTitles, string track, MediaPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(track))
            return false;

        string platformName = platform.ToDisplayString().ToLower();
        if (string.IsNullOrEmpty(platformName))
            return false;

        string normalizedTrack = NormalizeForLooseMatch(track);
        if (string.IsNullOrEmpty(normalizedTrack))
            return false;

        foreach (var title in windowTitles)
        {
            if (!title.Contains(platformName, StringComparison.OrdinalIgnoreCase))
                continue;

            string normalizedTitle = NormalizeForLooseMatch(title);
            if (normalizedTitle.Contains(normalizedTrack, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
    public static bool HasReliableWindowMatch(IEnumerable<string> windowTitles, string track, string platformName)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(platformName))
            return false;

        string normalizedTrack = NormalizeForLooseMatch(track);
        if (string.IsNullOrEmpty(normalizedTrack))
            return false;

        foreach (var title in windowTitles)
        {
            if (!title.Contains(platformName, StringComparison.OrdinalIgnoreCase))
                continue;

            string normalizedTitle = NormalizeForLooseMatch(title);
            if (normalizedTitle.Contains(normalizedTrack, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
    public static string ExtractTitleFromWindow(string windowTitle, MediaPlatform platform)
    {
        return ExtractTitleFromWindow(windowTitle, platform.ToDisplayString());
    }
    public static string ExtractTitleFromWindow(string windowTitle, string platformFallback)
    {
        var title = windowTitle;

        title = Regex.Replace(title, @"^\(\d+\+?\)\s*", "");
        title = Regex.Replace(title, @"^[▶⏸▶️⏸️\s]*", "");
        title = Regex.Replace(title, @"^[▶⏸\s\d:]+\|", "").Trim();

        foreach (var sep in TitleSeparators)
        {
            int idx = title.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                title = title.Substring(0, idx);
            }
        }

        title = title.Trim();

        title = Regex.Replace(title, @"\s+[\-\|–•]\s*$", "");

        return string.IsNullOrEmpty(title) ? platformFallback : title;
    }
    public static (string Artist, string Track) ParseSpotifyTitle(string title)
    {
        int firstSep = title.IndexOf(" - ", StringComparison.Ordinal);
        if (firstSep > 0)
        {
            string artist = title.Substring(0, firstSep).Trim();
            string track = title.Substring(firstSep + 3).Trim();
            if (!string.IsNullOrEmpty(track))
            {
                return (artist, track);
            }
        }

        return ("Spotify", title.Trim());
    }
    public static string NormalizeForLooseMatch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string folded = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        bool lastWasSpace = false;

        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;

        return sb.ToString();
    }
}

public enum MediaPlatform
{
    Unknown,
    Spotify,
    YouTube,
    SoundCloud,
    AppleMusic,
    Browser,
    Facebook,
    TikTok,
    Instagram,
    Twitter,
    Twitch,
    Discord,
    Netflix,
    Tidal,
    Deezer,
    Bandcamp,
    Bilibili,
    Vimeo
}
public static class MediaPlatformExtensions
{
    public static string ToDisplayString(this MediaPlatform platform) => platform switch
    {
        MediaPlatform.Spotify => "Spotify",
        MediaPlatform.YouTube => "YouTube",
        MediaPlatform.SoundCloud => "SoundCloud",
        MediaPlatform.AppleMusic => "Apple Music",
        MediaPlatform.Browser => "Browser",
        MediaPlatform.Facebook => "Facebook",
        MediaPlatform.TikTok => "TikTok",
        MediaPlatform.Instagram => "Instagram",
        MediaPlatform.Twitter => "Twitter",
        MediaPlatform.Twitch => "Twitch",
        MediaPlatform.Discord => "Discord",
        MediaPlatform.Netflix => "Netflix",
        MediaPlatform.Tidal => "TIDAL",
        MediaPlatform.Deezer => "Deezer",
        MediaPlatform.Bandcamp => "Bandcamp",
        MediaPlatform.Bilibili => "Bilibili",
        MediaPlatform.Vimeo => "Vimeo",
        _ => ""
    };
    public static MediaPlatform ParsePlatform(string? value)
    {
        if (string.IsNullOrEmpty(value)) return MediaPlatform.Unknown;

        return value.ToLowerInvariant() switch
        {
            "spotify" => MediaPlatform.Spotify,
            "youtube" => MediaPlatform.YouTube,
            "soundcloud" => MediaPlatform.SoundCloud,
            "apple music" => MediaPlatform.AppleMusic,
            "browser" => MediaPlatform.Browser,
            "facebook" => MediaPlatform.Facebook,
            "tiktok" => MediaPlatform.TikTok,
            "instagram" => MediaPlatform.Instagram,
            "twitter" => MediaPlatform.Twitter,
            "twitch" => MediaPlatform.Twitch,
            "discord" => MediaPlatform.Discord,
            "vesktop" => MediaPlatform.Discord,
            "netflix" => MediaPlatform.Netflix,
            "tidal" => MediaPlatform.Tidal,
            "deezer" => MediaPlatform.Deezer,
            "bandcamp" => MediaPlatform.Bandcamp,
            "bilibili" => MediaPlatform.Bilibili,
            "vimeo" => MediaPlatform.Vimeo,
            _ => MediaPlatform.Unknown
        };
    }
}
