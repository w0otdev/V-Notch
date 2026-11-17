using System.Runtime.InteropServices;
using System.Text.Json;

namespace VNotch.Services;

internal static partial class YouTubeMetadataParsing
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_youtube_parse_iso8601_duration_ticks(char* isoPtr, int isoLen, out long outTicks);

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
