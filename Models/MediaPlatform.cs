using System.Runtime.InteropServices;
using VNotch.Interop;

namespace VNotch.Models;

public enum MediaPlatform
{
    Unknown,
    Spotify,
    YouTube,
    SoundCloud,
    AppleMusic,
    Browser,
    Instagram = 8, // Preserve existing platform IDs; 6 and 7 are retired.
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

public static partial class MediaPlatformExtensions
{
    [LibraryImport(NativeAbi.LibraryName)]
    private static unsafe partial int vn_platform_parse(char* valPtr, int valLen, out int outPlatform);

    [LibraryImport(NativeAbi.LibraryName)]
    private static unsafe partial int vn_platform_to_display_string(int platform, char* outBuf, int outCap, out int outLen);

    public static unsafe string ToDisplayString(this MediaPlatform platform)
    {
        char* buf = stackalloc char[32];
        _ = vn_platform_to_display_string((int)platform, buf, 32, out int len);
        return len > 0 ? new string(buf, 0, len) : string.Empty;
    }

    public static unsafe MediaPlatform ParsePlatform(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return MediaPlatform.Unknown;

        fixed (char* ptr = value)
        {
            _ = vn_platform_parse(ptr, value.Length, out int p);
            return (MediaPlatform)p;
        }
    }
}
