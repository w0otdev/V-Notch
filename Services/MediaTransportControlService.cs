using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public sealed partial class MediaTransportControlService
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_transport_should_rewind_track(
        long currentPosTicks,
        long startTimeTicks,
        long endTimeTicks,
        byte hasTimeline,
        long nowUtcTicks,
        long lastRewindUtcTicks,
        ReadOnlySpan<char> lastSessionId,
        int lastSessionLen,
        ReadOnlySpan<char> currentSessionId,
        int currentSessionLen,
        out long outStartTargetTicks,
        out double outRelativeSeconds,
        out byte outShouldRewind);

    [LibraryImport(NativeDll)]
    private static partial int vn_media_transport_clamp_seek_target(
        long targetTicks,
        long endTimeTicks,
        out long outClampedTicks);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_media_detection_build_session_instance_key(
        nint unkPtr,
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    private const string LogTag = "MEDIA-CTRL";

    private readonly Func<IMediaTransportSession?> _getActiveSession;
    private readonly Action<byte> _sendMediaKey;
    private readonly Func<string, bool> _isBrowserMediaSession;
    private readonly Func<MediaInfo, bool> _goBackInMediaTab;
    private readonly Func<DateTime> _utcNow;

    public MediaTransportControlService(Func<GlobalSystemMediaTransportControlsSession?> getActiveSession)
        : this(() => getActiveSession() is { } session ? new WindowsMediaTransportSession(session) : null, SendMediaKey)
    {
    }

    internal MediaTransportControlService(Func<IMediaTransportSession?> getActiveSession, Action<byte> sendMediaKey,
        Func<string, bool>? isBrowserMediaSession = null, Func<MediaInfo, bool>? goBackInMediaTab = null,
        Func<DateTime>? utcNow = null)
    {
        _getActiveSession = getActiveSession;
        _sendMediaKey = sendMediaKey;
        _isBrowserMediaSession = isBrowserMediaSession ?? MediaWindowActivator.IsBrowserMediaSession;
        _goBackInMediaTab = goBackInMediaTab ?? MediaWindowActivator.TryGoBackInMediaTab;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task PlayPauseAsync()
    {
        try
        {
            var session = _getActiveSession();
            bool success = false;
            if (session != null)
            {
                success = await session.TryTogglePlayPauseAsync();
            }
            if (!success)
            {
                _sendMediaKey(Win32Interop.VK_MEDIA_PLAY_PAUSE);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "PlayPause failed");
            _sendMediaKey(Win32Interop.VK_MEDIA_PLAY_PAUSE);
        }
    }

    public async Task NextTrackAsync()
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                var controls = TryGetControls(session);
                if (controls?.IsNextEnabled == true && await session.TrySkipNextAsync())
                {
                    return;
                }

                // Seek to timeline end when next-track handler is absent, triggering
                // player autoplay or playlist advance.
                if (await TrySeekToTimelineEdgeAsync(session, toEnd: true))
                {
                    RuntimeLog.Log(LogTag, "Next: skip unsupported, jumped to end of timeline");
                    return;
                }
            }
            _sendMediaKey(Win32Interop.VK_MEDIA_NEXT_TRACK);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "NextTrack failed");
            _sendMediaKey(Win32Interop.VK_MEDIA_NEXT_TRACK);
        }
    }

    public const double RestartThresholdSeconds = 3.0;
    public const double ConsecutiveClickWindowSeconds = 3.0;

    private DateTime _lastRewindUtc = DateTime.MinValue;
    private string _lastRewindSessionId = "";

    public async Task PreviousTrackAsync()
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                var controls = TryGetControls(session);
                var timeline = session.GetTimelineProperties();
                string sessionId = session.SourceAppUserModelId ?? "";

                // Use the player's reported position for the restart threshold.
                // LastUpdatedTime can carry over from the previous video, so
                // extrapolating it can turn the first seconds into a restart.
                TimeSpan currentPos = timeline?.Position ?? TimeSpan.Zero;
                DateTime nowUtc = _utcNow();

                bool shouldRewind = vn_media_transport_should_rewind_track(
                    currentPos.Ticks,
                    timeline?.StartTime.Ticks ?? 0L,
                    timeline?.EndTime.Ticks ?? 0L,
                    timeline != null ? (byte)1 : (byte)0,
                    nowUtc.Ticks,
                    _lastRewindUtc.Ticks,
                    _lastRewindSessionId.AsSpan(),
                    _lastRewindSessionId.Length,
                    sessionId.AsSpan(),
                    sessionId.Length,
                    out long startTargetTicks,
                    out double relativeSeconds,
                    out byte outRewind) == 0 && outRewind != 0;

                // If playing past the 3-second threshold and not a rapid consecutive click,
                // rewind / restart the track to the beginning.
                if (shouldRewind)
                {
                    _lastRewindUtc = nowUtc;
                    _lastRewindSessionId = sessionId;

                    TimeSpan startTarget = TimeSpan.FromTicks(startTargetTicks);
                    if (await session.TryChangePlaybackPositionAsync(startTarget.Ticks))
                    {
                        RuntimeLog.Log(LogTag, $"Previous: rewound track to {startTarget.TotalSeconds:F1}s (was at {relativeSeconds:F1}s)");
                        return;
                    }

                    if (await TrySeekToTimelineEdgeAsync(session, toEnd: false))
                    {
                        RuntimeLog.Log(LogTag, "Previous: rewound timeline edge");
                        return;
                    }
                }

                // If within initial 3 seconds or consecutive double-click, skip to previous track
                _lastRewindUtc = DateTime.MinValue;
                _lastRewindSessionId = "";

                if (controls?.IsPreviousEnabled == true && await session.TrySkipPreviousAsync())
                {
                    RuntimeLog.Log(LogTag, "Previous: skipped track via SMTC");
                    return;
                }

                var browserInfo = new MediaInfo { SourceAppId = sessionId };
                if (await Task.Run(() => _isBrowserMediaSession(sessionId)))
                {
                    // Outside a playlist, a browser may expose no previous-track
                    // action. The media key cannot navigate its watch history.
                    browserInfo.CurrentTrack = await session.GetTitleAsync() ?? string.Empty;
                    if (!AreSameSession(session, _getActiveSession())) return;

                    bool navigated = await Task.Run(() => _goBackInMediaTab(browserInfo));
                    RuntimeLog.Log(LogTag, navigated
                        ? "Previous: navigated back in the matching browser tab"
                        : "Previous: no unambiguous browser tab with an available Back button");
                    return;
                }
            }
            RuntimeLog.Log(LogTag, "Previous: sending VK_MEDIA_PREV_TRACK");
            _sendMediaKey(Win32Interop.VK_MEDIA_PREV_TRACK);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "PreviousTrack failed");
            _sendMediaKey(Win32Interop.VK_MEDIA_PREV_TRACK);
        }
    }

    internal static bool AreSameSession(IMediaTransportSession? a, IMediaTransportSession? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        object? idA = a.Identity;
        object? idB = b.Identity;
        if (ReferenceEquals(idA, idB)) return true;
        if (idA is null || idB is null) return false;
        if (Equals(idA, idB)) return true;

        return TryBuildNativeSessionKey(idA, a.SourceAppUserModelId, out string keyA) &&
               TryBuildNativeSessionKey(idB, b.SourceAppUserModelId, out string keyB) &&
               string.Equals(keyA, keyB, StringComparison.Ordinal);
    }

    private static bool TryBuildNativeSessionKey(object identity, string? sourceAppId, out string key)
    {
        string sourceApp = sourceAppId ?? string.Empty;
        if (identity is WinRT.IWinRTObject winRtObject)
        {
            nint ptr = winRtObject.NativeObject.ThisPtr;
            if (ptr != 0 && TryBuildNativeSessionKeyFromPtr(ptr, sourceApp, out key))
            {
                GC.KeepAlive(identity);
                return true;
            }
        }

        if (Marshal.IsComObject(identity))
        {
            nint unk = Marshal.GetIUnknownForObject(identity);
            try
            {
                if (unk != 0 && TryBuildNativeSessionKeyFromPtr(unk, sourceApp, out key))
                {
                    return true;
                }
            }
            finally
            {
                if (unk != 0) Marshal.Release(unk);
            }
        }

        key = string.Empty;
        return false;
    }

    private static bool TryBuildNativeSessionKeyFromPtr(nint unkPtr, string sourceApp, out string key)
    {
        Span<char> buf = stackalloc char[512];
        int rc = vn_media_detection_build_session_instance_key(
            unkPtr, sourceApp.AsSpan(), sourceApp.Length, buf, buf.Length, out int len);
        if (rc == -2 && len > buf.Length)
        {
            char[] heapBuf = new char[len];
            rc = vn_media_detection_build_session_instance_key(
                unkPtr, sourceApp.AsSpan(), sourceApp.Length, heapBuf, heapBuf.Length, out len);
            if (rc == 0 && len > 0)
            {
                key = new string(heapBuf, 0, len);
                return true;
            }
        }
        else if (rc == 0 && len > 0)
        {
            key = new string(buf[..len]);
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static MediaTransportControls? TryGetControls(IMediaTransportSession session)
    {
        try
        {
            return session.GetControls();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TrySeekToTimelineEdgeAsync(
        IMediaTransportSession session, bool toEnd)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline == null) return false;

            if (toEnd)
            {
                if (timeline.EndTime <= TimeSpan.Zero) return false;
                return await session.TryChangePlaybackPositionAsync(timeline.EndTime.Ticks);
            }

            return await session.TryChangePlaybackPositionAsync(timeline.StartTime.Ticks);
        }
        catch
        {
            return false;
        }
    }

    private static void SendMediaKey(byte key)
    {
        Win32Interop.SendExtendedKeyTap(key);
    }

    public async Task SeekAsync(TimeSpan position)
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                await session.TryChangePlaybackPositionAsync(position.Ticks);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "Seek failed");
        }
    }

    public async Task SeekRelativeAsync(double seconds)
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    var current = timeline.Position;
                    var target = current + TimeSpan.FromSeconds(seconds);
                    long maxTicks = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime.Ticks : long.MaxValue;
                    long clampedTicks = vn_media_transport_clamp_seek_target(target.Ticks, timeline.EndTime.Ticks, out long outClamped) == 0
                        ? outClamped
                        : Math.Clamp(target.Ticks, 0L, maxTicks);

                    await session.TryChangePlaybackPositionAsync(clampedTicks);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "SeekRelative failed");
        }
    }

    public async Task SeekToAbsoluteAsync(TimeSpan position)
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                var timeline = session.GetTimelineProperties();
                if (timeline != null)
                {
                    long maxTicks = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime.Ticks : long.MaxValue;
                    long clampedTicks = vn_media_transport_clamp_seek_target(position.Ticks, timeline.EndTime.Ticks, out long outClamped) == 0
                        ? outClamped
                        : Math.Clamp(position.Ticks, 0L, maxTicks);

                    await session.TryChangePlaybackPositionAsync(clampedTicks);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "SeekToAbsolute failed");
        }
    }
}
