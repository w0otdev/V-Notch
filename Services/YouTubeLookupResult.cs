using System.Globalization;
using System.Text;

namespace VNotch.Services;

public enum YouTubeLookupSource
{
    Unknown = 0,
    OEmbed = 1,
    DataApi = 2,
}

public sealed class YouTubeLookupResult
{
    public string? Id { get; init; }
    public string? Author { get; init; }
    public string? Title { get; init; }
    public TimeSpan Duration { get; init; }

    public string? ThumbnailUrl { get; init; }

    public YouTubeLookupSource Source { get; init; } = YouTubeLookupSource.Unknown;

    public bool TitleMatches(string otherTitle)
    {
        if (string.IsNullOrEmpty(Title) || string.IsNullOrEmpty(otherTitle))
        {
            return false;
        }

        string t1 = PlatformDetector.NormalizeForLooseMatch(Title);
        string t2 = PlatformDetector.NormalizeForLooseMatch(otherTitle);
        if (t1.Length == 0 || t2.Length == 0) return false;
        return t1.Contains(t2, StringComparison.Ordinal) || t2.Contains(t1, StringComparison.Ordinal);
    }
}
