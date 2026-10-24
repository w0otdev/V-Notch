using System.Runtime.InteropServices;

namespace VNotch.Services;

public enum YouTubeLookupSource
{
    Unknown = 0,
    OEmbed = 1,
    DataApi = 2,
}

public sealed partial class YouTubeLookupResult
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_youtube_title_matches(
        char* titlePtr, int titleLen, char* otherPtr, int otherLen, out byte outMatches);

    public string? Id { get; init; }
    public string? Author { get; init; }
    public string? Title { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
    public YouTubeLookupSource Source { get; init; } = YouTubeLookupSource.Unknown;

    public unsafe bool TitleMatches(string otherTitle)
    {
        if (string.IsNullOrEmpty(Title) || string.IsNullOrEmpty(otherTitle))
            return false;

        fixed (char* pT1 = Title)
        fixed (char* pT2 = otherTitle)
        {
            _ = vn_youtube_title_matches(pT1, Title.Length, pT2, otherTitle.Length, out byte matches);
            return matches != 0;
        }
    }
}
