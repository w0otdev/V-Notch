using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

internal static partial class MediaProgressHelpers
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_media_progress_has_logical_session_changed(
        char* lastAppPtr, int lastAppLen,
        char* lastKeyPtr, int lastKeyLen,
        char* curAppPtr, int curAppLen,
        char* curKeyPtr, int curKeyLen,
        out byte outChanged);

    public static void NormalizeStartupSnapshotTimestamp(MediaInfo info)
    {
        if (!info.IsPlaying || info.Position <= TimeSpan.Zero)
        {
            return;
        }

        var updatedUtc = info.LastUpdated.ToUniversalTime();
        var nowUtc = DateTimeOffset.UtcNow;
        if (updatedUtc > nowUtc.AddMilliseconds(250))
        {
            info.LastUpdated = nowUtc;
        }
    }

    public static bool IsLikelyBrowserProgressSource(MediaInfo info)
    {
        if (info.Platform == MediaPlatform.YouTube)
            return true;
        if (!string.IsNullOrWhiteSpace(info.YouTubeVideoId))
            return true;
        if (!string.IsNullOrWhiteSpace(info.SourceAppId) &&
            info.SourceAppId.Contains("YouTube", StringComparison.OrdinalIgnoreCase))
            return true;
        if (info.Platform == MediaPlatform.SoundCloud)
            return true;
        if (info.Platform == MediaPlatform.Browser)
            return true;
        if (PlatformDetector.IsBrowserApp(info.SourceAppId))
            return true;

        return false;
    }

    public static unsafe bool HasLogicalSessionChanged(
        string? lastSourceAppId,
        string? lastSessionInstanceKey,
        string? currentSourceAppId,
        string? currentSessionInstanceKey)
    {
        string la = lastSourceAppId ?? string.Empty;
        string lk = lastSessionInstanceKey ?? string.Empty;
        string ca = currentSourceAppId ?? string.Empty;
        string ck = currentSessionInstanceKey ?? string.Empty;

        fixed (char* pLa = la)
        fixed (char* pLk = lk)
        fixed (char* pCa = ca)
        fixed (char* pCk = ck)
        {
            _ = vn_media_progress_has_logical_session_changed(
                pLa, la.Length,
                pLk, lk.Length,
                pCa, ca.Length,
                pCk, ck.Length,
                out byte changed);
            return changed != 0;
        }
    }

    public static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return time.ToString(@"h\:mm\:ss");
        }
        return time.ToString(@"m\:ss");
    }
}
