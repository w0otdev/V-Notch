using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VNotch.Services;

public static partial class InputMonitorService
{
    private const int WM_INPUT = 0x00FF;

#pragma warning disable S101 // Win32 POINT struct name
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
#pragma warning restore S101
    {
        public int x;
        public int y;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_input_monitor_is_left_button_down(
        ReadOnlySpan<byte> packetPtr,
        int packetLen,
        int ptrSize,
        out byte outIsDown);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_input_register_raw_mouse(long hwnd, byte remove, out int lastError);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_input_read_left_click(long rawInput, out byte leftDown, out int x, out int y);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_input_monitor_decode_message_position(
        uint packed,
        out int outX,
        out int outY);

    private static readonly HwndSourceHook InputHook = OnWindowMessage;
    private static HwndSource? _source;
    internal static bool IsStarted => _source != null;

    public static event EventHandler<POINT>? MouseActionTriggered;

    public static void Start(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(windowHandle);
        if (source == null || source.IsDisposed || ReferenceEquals(source, _source)) return;
        source.Dispatcher.VerifyAccess();
        Stop();
        source.AddHook(InputHook);

        // Raw input is delivered asynchronously to our window. A busy dispatcher
        // cannot hold up the system mouse hook chain. Keep ordinary mouse messages.
        if (vn_input_register_raw_mouse(windowHandle, 0, out int lastError) != 0)
        {
            source.RemoveHook(InputHook);
            RuntimeLog.Warn("INPUT", $"Raw mouse registration failed: {lastError}");
            return;
        }
        _source = source;
    }

    public static void Stop()
    {
        var source = _source;
        if (source == null) return;
        source.Dispatcher.VerifyAccess();
        vn_input_register_raw_mouse(0, 1, out _);
        if (!source.IsDisposed) source.RemoveHook(InputHook);
        _source = null;
    }

    private static IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_INPUT && _source != null &&
            vn_input_read_left_click(lParam, out byte leftDown, out int x, out int y) == 0 && leftDown != 0)
        {
            MouseActionTriggered?.Invoke(null, new POINT { x = x, y = y });
        }
        // Let WPF/DefWindowProc perform WM_INPUT cleanup.
        return IntPtr.Zero;
    }

    internal static bool IsLeftButtonDown(ReadOnlySpan<byte> packet)
    {
        return vn_input_monitor_is_left_button_down(packet, packet.Length, IntPtr.Size, out byte isDown) == 0 && isDown != 0;
    }

    internal static POINT DecodeMessagePosition(uint packed)
    {
        _ = vn_input_monitor_decode_message_position(packed, out int x, out int y);
        return new POINT { x = x, y = y };
    }
}
