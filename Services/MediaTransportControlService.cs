using System;
using System.Threading.Tasks;
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

                // Browser videos (e.g. YouTube outside a playlist) register no next-track
                // handler, so both SMTC skip and the media key are no-ops for them.
                // Jumping to the end of the timeline finishes the video and lets the
                // player advance on its own (autoplay / playlist).
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

    public async Task PreviousTrackAsync()
    {
        try
        {
            var session = _getActiveSession();
            if (session != null)
            {
                var controls = TryGetControls(session);
                if (controls?.IsPreviousEnabled == true && await session.TrySkipPreviousAsync())
                {
                    return;
                }

                // Same story as NextTrackAsync: with no previous-track handler the only
                // meaningful "previous" for a browser video is restarting it.
                if (await TrySeekToTimelineEdgeAsync(session, toEnd: false))
                {
                    RuntimeLog.Log(LogTag, "Previous: skip unsupported, restarted timeline");
                    return;
                }
            }
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
