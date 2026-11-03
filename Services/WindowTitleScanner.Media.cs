using System.Runtime.InteropServices;

namespace VNotch.Services;

public sealed record MediaWindowTitle(MediaPlatform Platform, string ExtractedTitle, string Track, string Artist);

public sealed partial class WindowTitleScanner
{
    private DateTime _lastMediaTitleScan, _lastRecoveryTitleScan;
    private MediaWindowTitle? _mediaTitle, _recoveryTitle;
    private DateTime _lastYouTubeMatchScan;
    private string _youTubeMatchTrack = "";
    private bool _youTubeMatch;

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_wtscanner_has_youtube_title(ReadOnlySpan<char> track, int length, uint excludedPid, out byte found);

    public bool HasYouTubeWindowMatch(string track, bool isThrottled)
    {
        lock (_cacheLock)
        {
            DateTime now = DateTime.UtcNow;
            int ttl = _titleNotificationsActive ? 30_000 : isThrottled ? 2000 : 700;
            if (_youTubeMatchTrack == track && (now - _lastYouTubeMatchScan).TotalMilliseconds < ttl) return _youTubeMatch;
            _youTubeMatch = WindowEnumerator != null
                ? PlatformDetector.HasReliableWindowMatch(GetAllWindowTitles(isThrottled), track, "YouTube")
                : vn_wtscanner_has_youtube_title(track, track.Length, (uint)Environment.ProcessId, out byte found) == 0 && found != 0;
            _youTubeMatchTrack = track;
            _lastYouTubeMatchScan = now;
            return _youTubeMatch;
        }
    }

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int vn_wtscanner_media_title(char* title, int length, uint excludedPid, byte recovery,
        Span<char> output, int capacity, out int platform, out int extractedLength, out int trackLength, out int artistLength);

    public MediaWindowTitle? FindMediaWindowTitle(bool recovery, bool isThrottled)
    {
        lock (_cacheLock)
        {
            DateTime now = DateTime.UtcNow;
            ref DateTime scanned = ref (recovery ? ref _lastRecoveryTitleScan : ref _lastMediaTitleScan);
            int ttl = _titleNotificationsActive ? 30_000 : isThrottled ? 2000 : 700;
            if ((now - scanned).TotalMilliseconds < ttl) return recovery ? _recoveryTitle : _mediaTitle;
            MediaWindowTitle? result = null;
            if (WindowEnumerator != null)
            {
                foreach (string title in GetAllWindowTitles(isThrottled))
                    if ((result = ParseMediaWindowTitle(title, recovery)) != null) break;
            }
            else result = ScanMediaWindowTitle(null, recovery);
            if (recovery) _recoveryTitle = result; else _mediaTitle = result;
            scanned = now;
            return result;
        }
    }

    internal static MediaWindowTitle? ParseMediaWindowTitle(string title, bool recovery) => ScanMediaWindowTitle(title, recovery);

    private static unsafe MediaWindowTitle? ScanMediaWindowTitle(string? title, bool recovery)
    {
        Span<char> buffer = stackalloc char[12288];
        fixed (char* pointer = title)
        {
            if (vn_wtscanner_media_title(pointer, title?.Length ?? 0, (uint)Environment.ProcessId, recovery ? (byte)1 : (byte)0,
                    buffer, buffer.Length, out int platform, out int extracted, out int track, out int artist) != 0 || platform == 0) return null;
            return new((MediaPlatform)platform, new(buffer[..extracted]), new(buffer.Slice(extracted, track)), new(buffer.Slice(extracted + track, artist)));
        }
    }
}
