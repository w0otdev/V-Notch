using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public sealed partial class MediaDetectionService : IMediaDetectionService, IAsyncDisposable
{
    private static string BuildTrackIdentity(string track, string artist)
        => MediaHeuristics.BuildTrackIdentity(track, artist);

    private bool IsStillSamePublishedTrack(string expectedTrack, string expectedArtist, string expectedSourceAppId, string expectedSessionInstanceKey = "")
        => PublishedTrackMatcher.IsSameTrack(
            new PublishedTrackSnapshot(
                _lastPublishedTrackIdentity,
                _lastPublishedTrackOnlyIdentity,
                _lastPublishedSourceAppId,
                _lastPublishedSessionInstanceKey),
            expectedTrack, expectedArtist, expectedSourceAppId, expectedSessionInstanceKey);

    private static string BuildSourceOverrideKey(string sessionInstanceKey, string sourceAppId)
        => MediaHeuristics.BuildSourceOverrideKey(sessionInstanceKey, sourceAppId);

    private static bool HasReliablePlatformWindowMatch(IEnumerable<string> windowTitles, string track, string platform)
    {
        return PlatformDetector.HasReliableWindowMatch(windowTitles, track, platform);
    }

    private bool TryGetSessionSourceOverride(MediaInfo info, out string sessionOverride)
    {
        sessionOverride = string.Empty;

        string key = BuildSourceOverrideKey(info.SessionInstanceKey, info.SourceAppId);
        if (string.IsNullOrEmpty(key) ||
            !_sessionState.TryGetSourceOverride(key, out string? resolvedOverride) ||
            string.IsNullOrWhiteSpace(resolvedOverride))
        {
            return false;
        }

        sessionOverride = resolvedOverride;
        return true;
    }

    private void SetSessionSourceOverride(MediaInfo info, string mediaSource)
    {
        string key = BuildSourceOverrideKey(info.SessionInstanceKey, info.SourceAppId);
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(mediaSource))
        {
            return;
        }

        _sessionState.SetSourceOverride(key, mediaSource);
    }

    private void ClearSessionSourceOverride(string sessionInstanceKey, string sourceAppId)
    {
        string key = BuildSourceOverrideKey(sessionInstanceKey, sourceAppId);
        if (!string.IsNullOrEmpty(key))
        {
            _sessionState.RemoveSourceOverride(key);
        }
    }

    private static string BuildSessionInstanceKey(GlobalSystemMediaTransportControlsSession session)
    {
        string sourceApp = session.SourceAppUserModelId ?? "";
        var native = ((WinRT.IWinRTObject)session).NativeObject;
        Span<char> buf = stackalloc char[512];
        try
        {
            int rc = VNotchNative.vn_media_detection_build_session_instance_key(
                native.ThisPtr, sourceApp.AsSpan(), sourceApp.Length, buf, buf.Length, out int len);
            if (rc == -2 && len > buf.Length)
            {
                char[] heapBuf = new char[len];
                rc = VNotchNative.vn_media_detection_build_session_instance_key(
                    native.ThisPtr, sourceApp.AsSpan(), sourceApp.Length, heapBuf, heapBuf.Length, out len);
                return rc == 0 && len > 0 ? new string(heapBuf, 0, len) : sourceApp;
            }
            return rc == 0 && len > 0 ? new string(buf[..len]) : sourceApp;
        }
        finally
        {
            GC.KeepAlive(session);
        }
    }

    private static bool AreSameSession(
        GlobalSystemMediaTransportControlsSession? a,
        GlobalSystemMediaTransportControlsSession? b)
    {
        return ReferenceEquals(a, b) ||
               (a != null && b != null && BuildSessionInstanceKey(a) == BuildSessionInstanceKey(b));
    }

    private static bool IsSessionPlayingStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private bool IsSessionStillPresent(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var sessions = _sessionManager?.GetSessions();
            if (sessions == null) return false;

            return sessions.Any(s => AreSameSession(s, session));
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("MEDIA-SESSION-PRESENT", ex.ToString());
            return true;
        }
    }

    private static bool IsBrowserSourceApp(string sourceAppId)
    {
        return PlatformDetector.IsBrowserApp(sourceAppId);
    }

    private static string? ExtractProcessNameFromSourceApp(string sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId)) return null;
        Span<char> buf = sourceAppId.Length <= 256 ? stackalloc char[256] : new char[sourceAppId.Length];
        return vn_media_detection_extract_process_name(
            sourceAppId.AsSpan(), sourceAppId.Length, buf, buf.Length, out int len) == 0 && len > 0
            ? new string(buf[..len])
            : null;
    }

    private void DetectPictureInPictureState(MediaInfo info)
    {
        if (!info.IsAnyMediaPlaying && string.IsNullOrEmpty(info.CurrentTrack))
        {
            info.IsPictureInPicture = false;
            return;
        }

        string? procName = ExtractProcessNameFromSourceApp(info.SourceAppId);
        bool isBrowser = info.Platform == MediaPlatform.Browser || IsBrowserSourceApp(info.SourceAppId);

        bool isPip = _windowTitleScanner.IsPipActive(procName);
        if (!isPip && isBrowser)
        {
            isPip = _windowTitleScanner.IsPipActive(null);
        }

        info.IsPictureInPicture = isPip;
    }

    private static bool IsIgnoredSourceApp(string sourceAppId)
        => MediaHeuristics.IsIgnoredSourceApp(sourceAppId);

    private static string DetectPlatformHint(IEnumerable<string> windowTitles)
    {
        return PlatformDetector.DetectPlatformHint(windowTitles);
    }

    private static string NormalizeForLooseMatch(string value)
    {
        return PlatformDetector.NormalizeForLooseMatch(value);
    }

    private static bool IsLikelySoundCloudPlaceholderThumbnail(BitmapSource? thumbnail)
        => ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(thumbnail);

    private static bool IsLikelySoundCloudArtworkCandidate(BitmapSource? thumbnail)
        => ThumbnailHeuristics.IsLikelyArtworkCandidate(thumbnail);

    private static bool IsLikelySoundCloudPlaceholderArtworkUrl(string? url)
        => MediaHeuristics.IsLikelySoundCloudPlaceholderArtworkUrl(url);

}
