using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VNotch.Services;

/// <summary>
/// Thin shell over the native Win32 layer (Native/Services/Win32Interop.cpp).
/// Public names and signatures are kept for UI callers; every call lands in VNotch.Native
/// instead of user32/kernel32/psapi/dwmapi directly.
/// </summary>
#pragma warning disable S101, CA1707, CA1708, IDE1006 // Native Win32 API struct and naming conventions
internal static unsafe partial class Win32Interop
{
    private const string NativeDll = "VNotch.Native.dll";

    #region Native exports

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_foreground_window")]
    private static partial int NativeGetForegroundWindow(out long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_set_foreground_window")]
    private static partial int NativeSetForegroundWindow(long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_window")]
    private static partial int NativeGetWindow(long hwnd, uint command, out long result);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_window_from_point")]
    private static partial int NativeWindowFromPoint(int x, int y, out long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_window_long")]
    private static partial int NativeGetWindowLong(long hwnd, int index, out int value);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_set_window_long")]
    private static partial int NativeSetWindowLong(long hwnd, int index, int value, out int previous);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_set_window_pos")]
    private static partial int NativeSetWindowPos(long hwnd, long insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_window_rect")]
    private static partial int NativeGetWindowRect(long hwnd, out RECT rect);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_window_thread_process_id")]
    private static partial int NativeGetWindowThreadProcessId(long hwnd, out uint processId, out uint threadId);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_dpi_for_window")]
    private static partial int NativeGetDpiForWindow(long hwnd, out uint dpi);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_cursor_pos")]
    private static partial int NativeGetCursorPos(out POINT point);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_system_metrics")]
    private static partial int NativeGetSystemMetrics(int index, out int value);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_async_key_state")]
    private static partial int NativeGetAsyncKeyState(int vk, out short state);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_send_extended_key_tap")]
    private static partial int NativeSendExtendedKeyTap(byte vk);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_register_hotkey")]
    private static partial int NativeRegisterHotKey(long hwnd, int id, uint modifiers, uint vk, out int lastError);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_unregister_hotkey")]
    private static partial int NativeUnregisterHotKey(long hwnd, int id);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_set_window_display_affinity")]
    private static partial int NativeSetWindowDisplayAffinity(long hwnd, uint affinity, out int lastError);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_dwm_set_window_attribute_int")]
    private static partial int NativeDwmSetWindowAttributeInt(long hwnd, int attribute, int value, out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_delete_gdi_object")]
    private static partial int NativeDeleteGdiObject(long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_destroy_icon")]
    private static partial int NativeDestroyIcon(long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_display_frequency", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeGetDisplayFrequency(ReadOnlySpan<char> deviceName, int deviceNameLength, out uint hz);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_invalidate_desktop_host_cache")]
    private static partial int NativeInvalidateDesktopHostCache();

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_is_window_directly_above_desktop")]
    private static partial int NativeIsWindowDirectlyAboveDesktop(long window, out byte isAbove, out long requiredAnchor);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_desktop_layer_insert_after")]
    private static partial int NativeGetDesktopLayerInsertAfter(long window, out long insertAfter);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_keyboard_hook_install")]
    private static partial int NativeKeyboardHookInstall(nint callback, long context, out int slot, out int lastError);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_keyboard_hook_uninstall")]
    private static partial int NativeKeyboardHookUninstall(int slot);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_current_process")]
    private static partial int NativeGetCurrentProcess(out long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_system_times")]
    private static partial int NativeGetSystemTimes(out FILETIME_METRICS idle, out FILETIME_METRICS kernel, out FILETIME_METRICS user);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_process_times")]
    private static partial int NativeGetProcessTimes(long process, out FILETIME_METRICS kernel, out FILETIME_METRICS user);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_current_thread_times")]
    private static partial int NativeGetCurrentThreadTimes(out FILETIME_METRICS kernel, out FILETIME_METRICS user);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_process_handle_count")]
    private static partial int NativeGetProcessHandleCount(long process, out uint count);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_process_memory_info")]
    private static partial int NativeGetProcessMemoryInfo(long process, out PROCESS_MEMORY_COUNTERS_EX counters);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_global_memory_status")]
    private static partial int NativeGlobalMemoryStatus(out MEMORYSTATUSEX_METRICS status);

    #endregion

    #region Windows

    public static IntPtr GetForegroundWindow() =>
        NativeGetForegroundWindow(out long hwnd) == 0 ? (IntPtr)hwnd : IntPtr.Zero;

    public static bool SetForegroundWindow(IntPtr hWnd) => NativeSetForegroundWindow(hWnd) == 0;

    public static IntPtr GetWindow(IntPtr hWnd, uint uCmd) =>
        NativeGetWindow(hWnd, uCmd, out long result) == 0 ? (IntPtr)result : IntPtr.Zero;

    public static IntPtr WindowFromPoint(POINT point) =>
        NativeWindowFromPoint(point.X, point.Y, out long hwnd) == 0 ? (IntPtr)hwnd : IntPtr.Zero;

    public static int GetWindowLong(IntPtr hWnd, int nIndex) =>
        NativeGetWindowLong(hWnd, nIndex, out int value) == 0 ? value : 0;

    public static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong) =>
        NativeSetWindowLong(hWnd, nIndex, dwNewLong, out int previous) == 0 ? previous : 0;

    public static bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags) =>
        NativeSetWindowPos(hWnd, hWndInsertAfter, X, Y, cx, cy, uFlags) == 0;

    public static bool GetWindowRect(IntPtr hWnd, out RECT lpRect) =>
        NativeGetWindowRect(hWnd, out lpRect) == 0;

    public static uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId)
    {
        NativeGetWindowThreadProcessId(hWnd, out lpdwProcessId, out uint threadId);
        return threadId;
    }

    public static uint GetDpiForWindow(IntPtr hwnd) =>
        NativeGetDpiForWindow(hwnd, out uint dpi) == 0 ? dpi : 0;

    public static bool GetCursorPos(out POINT lpPoint) => NativeGetCursorPos(out lpPoint) == 0;

    public static int GetSystemMetrics(int nIndex) =>
        NativeGetSystemMetrics(nIndex, out int value) == 0 ? value : 0;

    public static short GetAsyncKeyState(int vKey) =>
        NativeGetAsyncKeyState(vKey, out short state) == 0 ? state : (short)0;

    /// <summary>Presses and releases an extended virtual key (media keys).</summary>
    public static void SendExtendedKeyTap(byte virtualKey) => NativeSendExtendedKeyTap(virtualKey);

    public static bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk)
    {
        if (NativeRegisterHotKey(hWnd, id, fsModifiers, vk, out int lastError) == 0) return true;
        Marshal.SetLastPInvokeError(lastError);
        return false;
    }

    public static bool UnregisterHotKey(IntPtr hWnd, int id) => NativeUnregisterHotKey(hWnd, id) == 0;

    internal static bool SetWindowDisplayAffinityNative(IntPtr hWnd, uint dwAffinity)
    {
        if (NativeSetWindowDisplayAffinity(hWnd, dwAffinity, out int lastError) == 0) return true;
        Marshal.SetLastPInvokeError(lastError);
        return false;
    }

    public static bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity) =>
        WindowAnimationPolicy.SetCaptureAffinity(hWnd, dwAffinity);

    public static int DwmSetWindowAttributeInt(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute)
    {
        NativeDwmSetWindowAttributeInt(hwnd, dwAttribute, pvAttribute, out int hresult);
        return hresult;
    }

    public static bool DeleteObject(IntPtr hObject) => NativeDeleteGdiObject(hObject) == 0;

    public static bool DestroyIcon(IntPtr hIcon) => NativeDestroyIcon(hIcon) == 0;

    /// <summary>Current refresh rate of the named display device (null = primary), or null when unknown.</summary>
    public static int? GetDisplayRefreshHz(string? deviceName)
    {
        ReadOnlySpan<char> name = deviceName.AsSpan();
        return NativeGetDisplayFrequency(name, name.Length, out uint hz) == 0 && hz > 1 ? (int)hz : null;
    }

    #endregion

    #region Windows (visibility, clipboard listener, DPI, capture-tool detection)

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_is_window_visible")]
    private static partial int NativeIsWindowVisible(long hwnd, out byte visible);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_is_iconic")]
    private static partial int NativeIsIconic(long hwnd, out byte iconic);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_add_clipboard_format_listener")]
    private static partial int NativeAddClipboardFormatListener(long hwnd, out int lastError);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_remove_clipboard_format_listener")]
    private static partial int NativeRemoveClipboardFormatListener(long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_monitor_dpi_for_window")]
    private static partial int NativeGetMonitorDpiForWindow(long hwnd, uint monitorFlags, out uint dpiX, out uint dpiY);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_window_class_contains", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeWindowClassContains(long hwnd, ReadOnlySpan<char> needle, int needleLength, out byte matches);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_enum_large_visible_windows", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeEnumLargeVisibleWindows(
        int minSize, ReadOnlySpan<char> classNeedle, int classNeedleLength,
        VnLargeWindow* buffer, int capacity, out int written);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnLargeWindow
    {
        public long Hwnd;
        public uint ProcessId;
        public byte ClassMatches;
    }

    public static bool IsWindowVisible(IntPtr hWnd) => NativeIsWindowVisible(hWnd, out byte visible) == 0 && visible != 0;

    public static bool IsIconic(IntPtr hWnd) => NativeIsIconic(hWnd, out byte iconic) == 0 && iconic != 0;

    public static bool AddClipboardFormatListener(IntPtr hwnd)
    {
        if (NativeAddClipboardFormatListener(hwnd, out int lastError) == 0) return true;
        Marshal.SetLastPInvokeError(lastError);
        return false;
    }

    public static bool RemoveClipboardFormatListener(IntPtr hwnd) => NativeRemoveClipboardFormatListener(hwnd) == 0;

    /// <summary>Effective horizontal DPI of the monitor nearest to the window, or 0 when unavailable.</summary>
    public static uint GetMonitorDpiForWindow(IntPtr hwnd, uint monitorFlags = MONITOR_DEFAULTTONEAREST) =>
        NativeGetMonitorDpiForWindow(hwnd, monitorFlags, out uint dpiX, out _) == 0 ? dpiX : 0;

    /// <summary>True when the window class name contains <paramref name="needle"/> (ordinal, ignore case).</summary>
    public static bool WindowClassContains(IntPtr hwnd, string needle) =>
        NativeWindowClassContains(hwnd, needle, needle.Length, out byte matches) == 0 && matches != 0;

    /// <summary>
    /// Visible top-level windows at least <paramref name="minSize"/> pixels in both dimensions.
    /// Returns the number of entries written to <paramref name="buffer"/>.
    /// </summary>
    internal static int EnumLargeVisibleWindows(int minSize, string classNeedle, Span<VnLargeWindow> buffer)
    {
        if (buffer.IsEmpty) return 0;
        fixed (VnLargeWindow* pinned = buffer)
        {
            return NativeEnumLargeVisibleWindows(minSize, classNeedle, classNeedle.Length, pinned, buffer.Length, out int written) == 0
                ? written
                : 0;
        }
    }

    #endregion

    #region Timers, DWM and GDI capture

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_time_begin_period")]
    private static partial int NativeTimeBeginPeriod(uint periodMs, out uint result);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_time_end_period")]
    private static partial int NativeTimeEndPeriod(uint periodMs);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_dwm_flush")]
    private static partial int NativeDwmFlush(out int hresult);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_create_high_resolution_timer")]
    private static partial int NativeCreateHighResolutionTimer(out long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_set_waitable_timer")]
    private static partial int NativeSetWaitableTimer(long timer, long due100ns);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_cancel_waitable_timer")]
    private static partial int NativeCancelWaitableTimer(long timer);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_wait_for_any_of_two")]
    private static partial int NativeWaitForAnyOfTwo(long first, long second, uint timeoutMs, out int result);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_screen_dc")]
    private static partial int NativeGetScreenDc(out long dc);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_release_screen_dc")]
    private static partial int NativeReleaseScreenDc(long dc);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_capture_screen_rect")]
    private static partial int NativeCaptureScreenRect(long destDc, int width, int height, long sourceDc, int srcX, int srcY);

    public const int WAIT_OBJECT_0 = 0;
    public const int WAIT_TIMEOUT = 0x102;

    public static uint TimeBeginPeriod(uint period)
    {
        NativeTimeBeginPeriod(period, out uint result);
        return result;
    }

    public static uint TimeEndPeriod(uint period) => NativeTimeEndPeriod(period) == 0 ? 0u : 1u;

    public static int DwmFlush()
    {
        NativeDwmFlush(out int hresult);
        return hresult;
    }

    /// <summary>High-resolution waitable timer, or null when the OS cannot create one.</summary>
    public static SafeWaitHandle? CreateHighResolutionTimer() =>
        NativeCreateHighResolutionTimer(out long handle) == 0 ? new SafeWaitHandle((IntPtr)handle, ownsHandle: true) : null;

    /// <summary>Arms the timer to fire once after <paramref name="milliseconds"/>.</summary>
#pragma warning disable S3869 // Handle lifetime is pinned via DangerousAddRef/DangerousRelease
    public static bool SetWaitableTimerRelative(SafeWaitHandle timer, double milliseconds)
    {
        bool refAdded = false;
        try
        {
            timer.DangerousAddRef(ref refAdded);
            return NativeSetWaitableTimer(timer.DangerousGetHandle(), -Math.Max(1L, (long)(milliseconds * 10_000.0))) == 0;
        }
        finally
        {
            if (refAdded) timer.DangerousRelease();
        }
    }

    public static bool CancelWaitableTimer(SafeWaitHandle timer)
    {
        bool refAdded = false;
        try
        {
            timer.DangerousAddRef(ref refAdded);
            return NativeCancelWaitableTimer(timer.DangerousGetHandle()) == 0;
        }
        finally
        {
            if (refAdded) timer.DangerousRelease();
        }
    }
#pragma warning restore S3869

    /// <summary>Waits on two handles; returns 0 or 1 for the signaled one, <see cref="WAIT_TIMEOUT"/>, or -1 on failure.</summary>
    public static int WaitForAnyOfTwo(IntPtr first, IntPtr second, uint timeoutMs) =>
        NativeWaitForAnyOfTwo(first, second, timeoutMs, out int result) == 0 ? result : -1;

    public static IntPtr GetScreenDc() => NativeGetScreenDc(out long dc) == 0 ? (IntPtr)dc : IntPtr.Zero;

    public static void ReleaseScreenDc(IntPtr dc) => NativeReleaseScreenDc(dc);

    /// <summary>Copies a screen rectangle into <paramref name="destDc"/> and flushes GDI so the DIB is readable.</summary>
    public static bool CaptureScreenRect(IntPtr destDc, int width, int height, IntPtr sourceDc, int srcX, int srcY) =>
        NativeCaptureScreenRect(destDc, width, height, sourceDc, srcX, srcY) == 0;

    #endregion

    #region Threads, monitors, window enumeration and text

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_current_thread_id")]
    private static partial int NativeGetCurrentThreadId(out uint threadId);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_attach_thread_input")]
    private static partial int NativeAttachThreadInput(uint attachId, uint attachTo, byte attach);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_bring_window_to_top")]
    private static partial int NativeBringWindowToTop(long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_show_window")]
    private static partial int NativeShowWindow(long hwnd, int command);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_monitor_from_point")]
    private static partial int NativeMonitorFromPoint(int x, int y, uint flags, out long monitor);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_monitor_from_window")]
    private static partial int NativeMonitorFromWindow(long hwnd, uint flags, out long monitor);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_monitor_info")]
    private static partial int NativeGetMonitorInfo(long monitor, out MONITORINFO info);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_dpi_for_monitor")]
    private static partial int NativeGetDpiForMonitor(long monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_enum_top_level_windows")]
    private static partial int NativeEnumTopLevelWindows(long* buffer, int capacity, out int written);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_window_text_length")]
    private static partial int NativeGetWindowTextLength(long hwnd, out int length);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_window_text")]
    private static partial int NativeGetWindowText(long hwnd, char* buffer, int capacity, out int written);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_class_name")]
    private static partial int NativeGetClassName(long hwnd, char* buffer, int capacity, out int written);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int EnumWindowsCapacity = 2048;

    public static uint GetCurrentThreadId() =>
        NativeGetCurrentThreadId(out uint threadId) == 0 ? threadId : 0;

    public static bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach) =>
        NativeAttachThreadInput(idAttach, idAttachTo, fAttach ? (byte)1 : (byte)0) == 0;

    public static bool BringWindowToTop(IntPtr hWnd) => NativeBringWindowToTop(hWnd) == 0;

    public static bool ShowWindow(IntPtr hWnd, int nCmdShow) => NativeShowWindow(hWnd, nCmdShow) == 0;

    public static IntPtr MonitorFromPoint(POINT pt, uint dwFlags) =>
        NativeMonitorFromPoint(pt.X, pt.Y, dwFlags, out long monitor) == 0 ? (IntPtr)monitor : IntPtr.Zero;

    public static IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags) =>
        NativeMonitorFromWindow(hwnd, dwFlags, out long monitor) == 0 ? (IntPtr)monitor : IntPtr.Zero;

    public static bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi)
    {
        if (NativeGetMonitorInfo(hMonitor, out var info) != 0) return false;
        lpmi = info;
        return true;
    }

    public static int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY) =>
        NativeGetDpiForMonitor(hmonitor, dpiType, out dpiX, out dpiY);

    /// <summary>
    /// Walks a native snapshot of the top-level windows (topmost first) until <paramref name="enumProc"/> returns false.
    /// </summary>
    public static bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam)
    {
        long* windows = stackalloc long[EnumWindowsCapacity];
        if (NativeEnumTopLevelWindows(windows, EnumWindowsCapacity, out int count) != 0) return false;
        for (int i = 0; i < count; i++)
        {
            if (!enumProc((IntPtr)windows[i], lParam)) break;
        }

        return true;
    }

    public static int GetWindowTextLength(IntPtr hWnd) =>
        NativeGetWindowTextLength(hWnd, out int length) == 0 ? length : 0;

    public static int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount) =>
        nMaxCount > 0 && NativeGetWindowText(hWnd, lpString, nMaxCount, out int written) == 0 ? written : 0;

    public static int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount)
    {
        if (nMaxCount <= 0) return 0;
        char* buffer = stackalloc char[Math.Min(nMaxCount, 1024)];
        if (NativeGetWindowText(hWnd, buffer, Math.Min(nMaxCount, 1024), out int written) != 0 || written <= 0) return 0;
        lpString.Clear();
        lpString.Append(buffer, written);
        return written;
    }

    public static int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount)
    {
        if (nMaxCount <= 0) return 0;
        char* buffer = stackalloc char[Math.Min(nMaxCount, 1024)];
        if (NativeGetClassName(hWnd, buffer, Math.Min(nMaxCount, 1024), out int written) != 0 || written <= 0) return 0;
        lpClassName.Clear();
        lpClassName.Append(buffer, written);
        return written;
    }

    #endregion

    #region GDI DIB surface

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct VnDibSurface
    {
        public long MemoryDc;
        public long Bitmap;
        public long PreviousBitmap;
        public long Bits;
        public int Width;
        public int Height;
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_gdi_create_dib_surface")]
    private static partial int NativeCreateDibSurface(long screenDc, int width, int height, out VnDibSurface surface);

    [LibraryImport(NativeDll, EntryPoint = "vn_gdi_destroy_dib_surface")]
    private static partial int NativeDestroyDibSurface(ref VnDibSurface surface);

    /// <summary>Top-down 32-bit DIB selected into a compatible memory DC; release with <see cref="DestroyDibSurface"/>.</summary>
    internal static bool CreateDibSurface(IntPtr screenDc, int width, int height, out VnDibSurface surface) =>
        NativeCreateDibSurface(screenDc, width, height, out surface) == 0;

    internal static void DestroyDibSurface(ref VnDibSurface surface) => NativeDestroyDibSurface(ref surface);

    #endregion

    #region Shell, clipboard and files

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_clipboard_sequence_number")]
    private static partial int NativeGetClipboardSequenceNumber(out uint sequence);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_get_clipboard_owner")]
    private static partial int NativeGetClipboardOwner(out long hwnd);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_lock_workstation")]
    private static partial int NativeLockWorkStation(out int lastError);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_set_rounded_window_region")]
    private static partial int NativeSetRoundedWindowRegion(
        long hwnd, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_create_hard_link", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeCreateHardLink(
        string destination, int destinationLength, string existing, int existingLength, out int lastError);

    public static uint GetClipboardSequenceNumber() =>
        NativeGetClipboardSequenceNumber(out uint sequence) == 0 ? sequence : 0;

    public static IntPtr GetClipboardOwner() =>
        NativeGetClipboardOwner(out long hwnd) == 0 ? (IntPtr)hwnd : IntPtr.Zero;

    public static bool LockWorkStation()
    {
        if (NativeLockWorkStation(out int lastError) == 0) return true;
        Marshal.SetLastPInvokeError(lastError);
        return false;
    }

    /// <summary>Clips the window to a rounded rectangle; the window owns the region on success.</summary>
    public static bool SetRoundedWindowRegion(IntPtr hwnd, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight) =>
        NativeSetRoundedWindowRegion(hwnd, left, top, right, bottom, ellipseWidth, ellipseHeight) == 0;

    public static bool CreateHardLink(string destination, string existing)
    {
        if (NativeCreateHardLink(destination, destination.Length, existing, existing.Length, out int lastError) == 0) return true;
        Marshal.SetLastPInvokeError(lastError);
        return false;
    }

    #endregion

    #region Desktop layer

    static Win32Interop()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => InvalidateDesktopHostCache();
        }
        catch
        {
            // SystemEvents may be unavailable in some hosting environments
        }
    }

    public static void InvalidateDesktopHostCache() => NativeInvalidateDesktopHostCache();

    public static bool IsWindowDirectlyAboveDesktop(IntPtr window, out IntPtr requiredAnchor)
    {
        if (NativeIsWindowDirectlyAboveDesktop(window, out byte isAbove, out long anchor) != 0)
        {
            requiredAnchor = HWND_NOTOPMOST;
            return false;
        }

        requiredAnchor = (IntPtr)anchor;
        return isAbove != 0;
    }

    public static IntPtr GetDesktopLayerInsertAfter(IntPtr window) =>
        NativeGetDesktopLayerInsertAfter(window, out long insertAfter) == 0 ? (IntPtr)insertAfter : HWND_NOTOPMOST;

    #endregion

    #region Low-level keyboard hook

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct KeyboardHookEvent
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public long ExtraInfo;
        public int Message;
    }

    /// <summary>
    /// WH_KEYBOARD_LL hook whose procedure lives in the native layer. Install and dispose on a thread
    /// that pumps messages; the handler runs on that thread and returns true to swallow the key.
    /// </summary>
    internal sealed class LowLevelKeyboardHook(Func<KBDLLHOOKSTRUCT, int, bool> handler) : IDisposable
    {
        private GCHandle _self;
        private int _slot = -1;

        internal int InstallationError { get; private set; }
        internal bool IsInstalled => Volatile.Read(ref _slot) >= 0;

        internal bool Install()
        {
            if (_slot >= 0) return true;
            _self = GCHandle.Alloc(this);
            delegate* unmanaged[Stdcall]<long, KeyboardHookEvent*, int> callback = &OnKey;
            if (NativeKeyboardHookInstall((nint)callback, GCHandle.ToIntPtr(_self), out int slot, out int lastError) != 0)
            {
                InstallationError = lastError;
                _self.Free();
                return false;
            }

            Volatile.Write(ref _slot, slot);
            return true;
        }

        public void Dispose()
        {
            int slot = Interlocked.Exchange(ref _slot, -1);
            if (slot < 0) return;
            NativeKeyboardHookUninstall(slot);
            if (_self.IsAllocated) _self.Free();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int OnKey(long context, KeyboardHookEvent* data)
        {
            try
            {
                if (GCHandle.FromIntPtr((IntPtr)context).Target is not LowLevelKeyboardHook hook) return 0;
                var key = new KBDLLHOOKSTRUCT
                {
                    vkCode = data->VkCode,
                    scanCode = data->ScanCode,
                    flags = data->Flags,
                    time = data->Time,
                    dwExtraInfo = (IntPtr)data->ExtraInfo,
                };
                return hook.Handle(key, data->Message) ? 1 : 0;
            }
            catch
            {
                // Exceptions must never unwind through user32 or delay the keyboard chain.
                return 0;
            }
        }

        private bool Handle(KBDLLHOOKSTRUCT key, int message) => handler(key, message);
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_win_event_hook_install")]
    private static partial int NativeWinEventHookInstall(uint eventMin, uint eventMax, nint callback, long context, out int slot);

    [LibraryImport(NativeDll, EntryPoint = "vn_win32_win_event_hook_uninstall")]
    private static partial int NativeWinEventHookUninstall(int slot);

    /// <summary>
    /// Out-of-context WinEvent hook for an event range, owned by the native layer. Install and dispose on a
    /// thread that pumps messages; the handler (event type, window) runs on that thread.
    /// </summary>
    internal sealed class WinEventHook(uint eventMin, uint eventMax, Action<uint, IntPtr> handler) : IDisposable
    {
        private readonly Action<uint, IntPtr> _handler = handler;
        private GCHandle _self;
        private int _slot = -1;

        internal bool IsInstalled => Volatile.Read(ref _slot) >= 0;

        internal bool Install()
        {
            if (_slot >= 0) return true;
            _self = GCHandle.Alloc(this);
            delegate* unmanaged[Stdcall]<long, uint, long, void> callback = &OnEvent;
            if (NativeWinEventHookInstall(eventMin, eventMax, (nint)callback, GCHandle.ToIntPtr(_self), out int slot) != 0)
            {
                _self.Free();
                return false;
            }

            Volatile.Write(ref _slot, slot);
            return true;
        }

        public void Dispose()
        {
            int slot = Interlocked.Exchange(ref _slot, -1);
            if (slot < 0) return;
            NativeWinEventHookUninstall(slot);
            if (_self.IsAllocated) _self.Free();
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void OnEvent(long context, uint eventType, long hwnd)
        {
            try
            {
                if (GCHandle.FromIntPtr((IntPtr)context).Target is WinEventHook hook)
                    hook._handler(eventType, (IntPtr)hwnd);
            }
            catch
            {
                // Exceptions must never unwind into the native event thunk.
            }
        }
    }

    #endregion

    #region Constants

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public const uint GW_HWNDPREV = 3;
    public const uint GW_HWNDNEXT = 2;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const int MDT_EFFECTIVE_DPI = 0;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_ACTIVATE = 0x0006;
    public const int WM_ACTIVATEAPP = 0x001C;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WH_KEYBOARD_LL = 13;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
    public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    public const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    public const byte VK_MEDIA_PREV_TRACK = 0xB1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const int VK_LBUTTON = 0x01;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWA_CLOAKED = 14;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;
    public const uint LLKHF_ALTDOWN = 0x20;

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    #endregion

    #region Performance & system metrics

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FILETIME_METRICS
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    public static bool GetSystemTimes(out FILETIME_METRICS lpIdleTime, out FILETIME_METRICS lpKernelTime, out FILETIME_METRICS lpUserTime) =>
        NativeGetSystemTimes(out lpIdleTime, out lpKernelTime, out lpUserTime) == 0;

    public static bool GetProcessTimes(IntPtr hProcess, out FILETIME_METRICS lpCreationTime, out FILETIME_METRICS lpExitTime, out FILETIME_METRICS lpKernelTime, out FILETIME_METRICS lpUserTime)
    {
        lpCreationTime = default;
        lpExitTime = default;
        return NativeGetProcessTimes(hProcess, out lpKernelTime, out lpUserTime) == 0;
    }

    /// <summary>Kernel and user CPU time consumed by the calling thread (100 ns units).</summary>
    public static bool GetCurrentThreadTimes(out FILETIME_METRICS kernel, out FILETIME_METRICS user) =>
        NativeGetCurrentThreadTimes(out kernel, out user) == 0;

    public static IntPtr GetCurrentProcess() =>
        NativeGetCurrentProcess(out long handle) == 0 ? (IntPtr)handle : IntPtr.Zero;

    public static bool GetProcessHandleCount(IntPtr hProcess, out uint pdwHandleCount) =>
        NativeGetProcessHandleCount(hProcess, out pdwHandleCount) == 0;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public ulong PeakWorkingSetSize;
        public ulong WorkingSetSize;
        public ulong QuotaPeakPagedPoolUsage;
        public ulong QuotaPagedPoolUsage;
        public ulong QuotaPeakNonPagedPoolUsage;
        public ulong QuotaNonPagedPoolUsage;
        public ulong PagefileUsage;
        public ulong PeakPagefileUsage;
        public ulong PrivateUsage;
    }

    public static bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX counters, uint cb) =>
        NativeGetProcessMemoryInfo(hProcess, out counters) == 0;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MEMORYSTATUSEX_METRICS
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    public static bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX_METRICS lpBuffer) =>
        NativeGlobalMemoryStatus(out lpBuffer) == 0;

    #endregion
}
