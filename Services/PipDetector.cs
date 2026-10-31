using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public static partial class PipDetector
{
    private const int ErrBufferTooSmall = -2;

    public static unsafe bool IsPipTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        fixed (char* ptr = title)
        {
            vn_pip_is_pip_title(ptr, title.Length, out byte isPip);
            return isPip != 0;
        }
    }

    public static unsafe bool IsKnownBrowserProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        fixed (char* ptr = processName)
        {
            vn_pip_is_known_browser_process(ptr, processName.Length, out byte isBrowser);
            return isBrowser != 0;
        }
    }

    public static unsafe bool TryFindPipWindow(
        out IntPtr pipHwnd,
        out string pipTitle,
        string? targetProcessName = null,
        string? currentTrack = null)
    {
        pipHwnd = IntPtr.Zero;
        pipTitle = string.Empty;

        string proc = targetProcessName ?? string.Empty;
        string track = currentTrack ?? string.Empty;
        Span<char> stackTitle = stackalloc char[512];
        int titleLen;
        byte found;
        int rc;

        fixed (char* pProc = proc)
        fixed (char* pTrack = track)
        fixed (char* pTitle = stackTitle)
        {
            rc = vn_pip_try_find_pip_window(
                pProc, proc.Length,
                pTrack, track.Length,
                out pipHwnd,
                pTitle, stackTitle.Length, out titleLen,
                out found);
        }

        if (rc == 0)
        {
            if (found == 0 || pipHwnd == IntPtr.Zero) return false;
            pipTitle = titleLen > 0 ? new string(stackTitle[..titleLen]) : string.Empty;
            return true;
        }

        if (rc != ErrBufferTooSmall || titleLen <= 0) return false;

        char[] rented = ArrayPool<char>.Shared.Rent(titleLen);
        try
        {
            fixed (char* pProc = proc)
            fixed (char* pTrack = track)
            fixed (char* pTitle = rented)
            {
                rc = vn_pip_try_find_pip_window(
                    pProc, proc.Length,
                    pTrack, track.Length,
                    out pipHwnd,
                    pTitle, rented.Length, out titleLen,
                    out found);
            }
            if (rc != 0 || found == 0 || pipHwnd == IntPtr.Zero) return false;
            pipTitle = titleLen > 0 ? new string(rented, 0, titleLen) : string.Empty;
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_pip_is_pip_title(
        char* title_ptr, int title_len, out byte out_is_pip);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_pip_is_known_browser_process(
        char* proc_ptr, int proc_len, out byte out_is_browser);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_pip_try_find_pip_window(
        char* target_proc_ptr, int target_proc_len,
        char* current_track_ptr, int current_track_len,
        out IntPtr out_hwnd,
        char* title_buf, int title_cap, out int out_title_len,
        out byte out_found);
}
