using System;
using System.Threading.Tasks;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public sealed class MediaTransportControlService
{
    private const string LogTag = "MEDIA-CTRL";

    private readonly Func<GlobalSystemMediaTransportControlsSession?> _getActiveSession;

    public MediaTransportControlService(Func<GlobalSystemMediaTransportControlsSession?> getActiveSession)
    {
        _getActiveSession = getActiveSession;
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
                SendMediaKey(Win32Interop.VK_MEDIA_PLAY_PAUSE);
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "PlayPause failed");
            SendMediaKey(Win32Interop.VK_MEDIA_PLAY_PAUSE);
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
            SendMediaKey(Win32Interop.VK_MEDIA_NEXT_TRACK);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "NextTrack failed");
            SendMediaKey(Win32Interop.VK_MEDIA_NEXT_TRACK);
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
                TimeSpan relativePos = timeline != null && currentPos >= timeline.StartTime
                    ? currentPos - timeline.StartTime
                    : currentPos;

                bool isConsecutive = (DateTime.UtcNow - _lastRewindUtc).TotalSeconds < ConsecutiveClickWindowSeconds
                    && string.Equals(_lastRewindSessionId, sessionId, StringComparison.OrdinalIgnoreCase);

                // If playing past the 3-second threshold and not a rapid consecutive click,
                // rewind / restart the track to the beginning.
                if (!isConsecutive && timeline != null && timeline.EndTime > TimeSpan.Zero && relativePos.TotalSeconds > RestartThresholdSeconds)
                {
                    _lastRewindUtc = DateTime.UtcNow;
                    _lastRewindSessionId = sessionId;

                    TimeSpan startTarget = timeline.StartTime > TimeSpan.Zero ? timeline.StartTime : TimeSpan.Zero;
                    if (await session.TryChangePlaybackPositionAsync(startTarget.Ticks))
                    {
                        RuntimeLog.Log(LogTag, $"Previous: rewound track to {startTarget.TotalSeconds:F1}s (was at {relativePos.TotalSeconds:F1}s)");
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
                if (await Task.Run(() => MediaWindowActivator.IsBrowserMediaSession(sessionId)))
                {
                    // Outside a playlist, a browser may expose no previous-track
                    // action. The media key cannot navigate its watch history.
                    var properties = await session.TryGetMediaPropertiesAsync();
                    browserInfo.CurrentTrack = properties?.Title ?? string.Empty;
                    if (!ReferenceEquals(session, _getActiveSession())) return;

                    bool navigated = await Task.Run(() => MediaWindowActivator.TryGoBackInMediaTab(browserInfo));
                    RuntimeLog.Log(LogTag, navigated
                        ? "Previous: navigated back in the matching browser tab"
                        : "Previous: no unambiguous browser tab with an available Back button");
                    return;
                }
            }
            RuntimeLog.Log(LogTag, "Previous: sending VK_MEDIA_PREV_TRACK");
            SendMediaKey(Win32Interop.VK_MEDIA_PREV_TRACK);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "PreviousTrack failed");
            SendMediaKey(Win32Interop.VK_MEDIA_PREV_TRACK);
        }
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackControls? TryGetControls(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.Controls;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TrySeekToTimelineEdgeAsync(
        GlobalSystemMediaTransportControlsSession session, bool toEnd)
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
        Win32Interop.keybd_event(key, 0, Win32Interop.KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        Win32Interop.keybd_event(key, 0, Win32Interop.KEYEVENTF_EXTENDEDKEY | Win32Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
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

                    if (target < TimeSpan.Zero)
                        target = TimeSpan.Zero;
                    if (timeline.EndTime > TimeSpan.Zero && target > timeline.EndTime)
                        target = timeline.EndTime;

                    await session.TryChangePlaybackPositionAsync(target.Ticks);
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
                    var target = position;
                    if (target < TimeSpan.Zero)
                        target = TimeSpan.Zero;
                    if (timeline.EndTime > TimeSpan.Zero && target > timeline.EndTime)
                        target = timeline.EndTime;

                    await session.TryChangePlaybackPositionAsync(target.Ticks);
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogTag, ex, "SeekToAbsolute failed");
        }
    }
}
