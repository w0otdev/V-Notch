using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace VNotch.Services;

internal readonly record struct MediaTransportControls(bool IsNextEnabled, bool IsPreviousEnabled);
internal sealed record MediaTransportTimeline(TimeSpan StartTime, TimeSpan EndTime, TimeSpan Position);

internal interface IMediaTransportSession
{
    object Identity { get; }
    string SourceAppUserModelId { get; }
    MediaTransportControls? GetControls();
    MediaTransportTimeline? GetTimelineProperties();
    Task<bool> TryTogglePlayPauseAsync();
    Task<bool> TrySkipNextAsync();
    Task<bool> TrySkipPreviousAsync();
    Task<bool> TryChangePlaybackPositionAsync(long ticks);
    Task<string?> GetTitleAsync();
}

internal sealed partial class WindowsMediaTransportSession(GlobalSystemMediaTransportControlsSession session) : IMediaTransportSession
{
    private const string NativeDll = "VNotch.Native.dll";
    private const int VN_ERR_BUFFER_TOO_SMALL = -2;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_gsmtc_get_source_app_id(
        nint sessionPtr,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll)]
    private static partial int vn_gsmtc_get_controls(
        nint sessionPtr,
        out byte outHasControls,
        out byte outIsNextEnabled,
        out byte outIsPrevEnabled);

    [LibraryImport(NativeDll)]
    private static partial int vn_gsmtc_get_timeline(
        nint sessionPtr,
        out byte outHasTimeline,
        out long outStartTicks,
        out long outEndTicks,
        out long outPositionTicks);

    [LibraryImport(NativeDll)]
    private static partial int vn_gsmtc_try_action(
        nint sessionPtr,
        int action,
        long positionTicks,
        out byte outSuccess);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_gsmtc_get_title(
        nint sessionPtr,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outHasTitle);

    private nint NativePtr => ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;

    public object Identity => session;

    public string SourceAppUserModelId
    {
        get
        {
            Span<char> buf = stackalloc char[512];
            try
            {
                int rc = vn_gsmtc_get_source_app_id(NativePtr, buf, buf.Length, out int len);
                if (rc == VN_ERR_BUFFER_TOO_SMALL && len > buf.Length)
                {
                    buf = new char[len];
                    rc = vn_gsmtc_get_source_app_id(NativePtr, buf, buf.Length, out len);
                }
                return rc == 0 && len > 0
                    ? new string(buf[..len])
                    : string.Empty;
            }
            finally
            {
                GC.KeepAlive(session);
            }
        }
    }

    public MediaTransportControls? GetControls()
    {
        try
        {
            return vn_gsmtc_get_controls(NativePtr, out byte hasControls, out byte isNext, out byte isPrev) == 0 && hasControls != 0
                ? new MediaTransportControls(isNext != 0, isPrev != 0)
                : null;
        }
        finally
        {
            GC.KeepAlive(session);
        }
    }

    public MediaTransportTimeline? GetTimelineProperties()
    {
        try
        {
            return vn_gsmtc_get_timeline(NativePtr, out byte hasTimeline, out long start, out long end, out long pos) == 0 && hasTimeline != 0
                ? new MediaTransportTimeline(TimeSpan.FromTicks(start), TimeSpan.FromTicks(end), TimeSpan.FromTicks(pos))
                : null;
        }
        finally
        {
            GC.KeepAlive(session);
        }
    }

    public Task<bool> TryTogglePlayPauseAsync() => RunActionAsync(0, 0);
    public Task<bool> TrySkipNextAsync() => RunActionAsync(1, 0);
    public Task<bool> TrySkipPreviousAsync() => RunActionAsync(2, 0);
    public Task<bool> TryChangePlaybackPositionAsync(long ticks) => RunActionAsync(3, ticks);

    private Task<bool> RunActionAsync(int action, long ticks) => Task.Run(() =>
    {
        try
        {
            return vn_gsmtc_try_action(NativePtr, action, ticks, out byte success) == 0 && success != 0;
        }
        finally
        {
            GC.KeepAlive(session);
        }
    });

    public Task<string?> GetTitleAsync() => Task.Run(() =>
    {
        Span<char> buf = stackalloc char[1024];
        try
        {
            int rc = vn_gsmtc_get_title(NativePtr, buf, buf.Length, out int len, out byte hasTitle);
            if (rc == VN_ERR_BUFFER_TOO_SMALL && len > buf.Length)
            {
                buf = new char[len];
                rc = vn_gsmtc_get_title(NativePtr, buf, buf.Length, out len, out hasTitle);
            }
            return rc == 0 && hasTitle != 0
                ? new string(buf[..len])
                : null;
        }
        finally
        {
            GC.KeepAlive(session);
        }
    });
}
