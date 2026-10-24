using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services;

internal static partial class YouTubeMetadataParsing
{
    private static readonly string[] ThumbnailPreference = { "maxres", "standard", "high", "medium", "default" };

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_youtube_looks_like_quota_exceeded(char* bodyPtr, int bodyLen, out byte outExceeded);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_youtube_parse_iso8601_duration_ticks(char* isoPtr, int isoLen, out long outTicks);

    public static string? PickBestThumbnail(JsonElement thumbnails)
    {
        foreach (var key in ThumbnailPreference)
        {
            if (thumbnails.TryGetProperty(key, out var el) &&
                el.TryGetProperty("url", out var urlEl))
            {
                string? value = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        return null;
    }

    public static unsafe bool LooksLikeQuotaExceeded(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        fixed (char* ptr = body)
        {
            _ = vn_youtube_looks_like_quota_exceeded(ptr, body.Length, out byte ex);
            return ex != 0;
        }
    }

    public static unsafe TimeSpan ParseIso8601Duration(string iso)
    {
        if (string.IsNullOrEmpty(iso))
            return TimeSpan.Zero;

        fixed (char* ptr = iso)
        {
            _ = vn_youtube_parse_iso8601_duration_ticks(ptr, iso.Length, out long ticks);
            return TimeSpan.FromTicks(ticks);
        }
    }
}
