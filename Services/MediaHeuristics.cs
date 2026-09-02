namespace VNotch.Services;

internal static class MediaHeuristics
{
    public static string BuildTrackIdentity(string track, string artist)
    {
        return $"{track.Trim().ToLowerInvariant()}|{artist.Trim().ToLowerInvariant()}";
    }

    public static string BuildSourceOverrideKey(string sessionInstanceKey, string sourceAppId)
    {
        if (!string.IsNullOrWhiteSpace(sessionInstanceKey))
        {
            return sessionInstanceKey;
        }

        return PlatformDetector.IsBrowserApp(sourceAppId) ? string.Empty : sourceAppId ?? string.Empty;
    }

    public static bool IsTrackCompatibleWithWindowTitle(string track, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(windowTitle))
            return false;

        string normalizedTrack = PlatformDetector.NormalizeForLooseMatch(track);
        string normalizedWindowTitle = PlatformDetector.NormalizeForLooseMatch(windowTitle);
        return !string.IsNullOrEmpty(normalizedTrack) &&
               normalizedWindowTitle.Contains(normalizedTrack, StringComparison.Ordinal);
    }

    public static bool IsIgnoredSourceApp(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
            return false;

        if (sourceAppId.Contains("V-Notch", StringComparison.OrdinalIgnoreCase) ||
            sourceAppId.Contains("vnotch", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        string normalized = url.Replace("\\u0026", "&").Replace("\\/", "/").ToLowerInvariant();
        return normalized.Contains("default_avatar", StringComparison.Ordinal) ||
               normalized.Contains("/images/default_", StringComparison.Ordinal) ||
               normalized.Contains("default-soundcloud", StringComparison.Ordinal) ||
               normalized.Contains("/avatars-", StringComparison.Ordinal);
    }

    public static string NormalizeTrackForComparison(string text)
    {
        var result = text
            .Replace(" (", " - ", StringComparison.Ordinal)
            .Replace("(", " - ", StringComparison.Ordinal)
            .Replace(")", "", StringComparison.Ordinal)
            .Replace(" [", " - ", StringComparison.Ordinal)
            .Replace("[", " - ", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal);
        return result.Trim();
    }

    public static string ExtractCoreTrackName(string track)
    {
        int parenIdx = track.IndexOf('(');
        int bracketIdx = track.IndexOf('[');
        int cutIdx = -1;

        if (parenIdx > 0 && bracketIdx > 0)
            cutIdx = Math.Min(parenIdx, bracketIdx);
        else if (parenIdx > 0)
            cutIdx = parenIdx;
        else if (bracketIdx > 0)
            cutIdx = bracketIdx;

        if (cutIdx > 0)
            return track.Substring(0, cutIdx).Trim();

        return track;
    }

    public static bool SpotifyTitleContainsTrack(string spotifyWindowTitle, string smtcTrack)
    {
        if (string.IsNullOrEmpty(spotifyWindowTitle) || string.IsNullOrEmpty(smtcTrack))
            return false;

        if (spotifyWindowTitle.Contains(smtcTrack, StringComparison.OrdinalIgnoreCase))
            return true;

        string normalizedTrack = NormalizeTrackForComparison(smtcTrack);
        string normalizedTitle = NormalizeTrackForComparison(spotifyWindowTitle);

        if (normalizedTitle.Contains(normalizedTrack, StringComparison.OrdinalIgnoreCase))
            return true;

        string coreTrack = ExtractCoreTrackName(smtcTrack);
        if (!string.IsNullOrEmpty(coreTrack) && coreTrack.Length >= 3 &&
            spotifyWindowTitle.Contains(coreTrack, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
