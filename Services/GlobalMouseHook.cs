using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace VNotch.Services;

public static class InputMonitorService
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

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
        var device = new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = windowHandle };
        if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            source.RemoveHook(InputHook);
            RuntimeLog.Warn("INPUT", $"Raw mouse registration failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        _source = source;
    }

    public static void Stop()
    {
        var source = _source;
        if (source == null) return;
        source.Dispatcher.VerifyAccess();
        var device = new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE };
        RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (!source.IsDisposed) source.RemoveHook(InputHook);
        _source = null;
    }

    private static unsafe IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_INPUT && _source != null)
        {
            // RAWINPUTHEADER + RAWMOUSE fits in 48 bytes on x64 (40 on x86).
            byte* buffer = stackalloc byte[64];
            uint size = 64;
            uint read = GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)(8 + 2 * IntPtr.Size));
            if (read != uint.MaxValue && read <= 64 && IsLeftButtonDown(new ReadOnlySpan<byte>(buffer, (int)read)))
                MouseActionTriggered?.Invoke(null, DecodeMessagePosition(GetMessagePos()));
        }
        // Let WPF/DefWindowProc perform WM_INPUT cleanup.
        return IntPtr.Zero;
    }

    internal static bool IsLeftButtonDown(ReadOnlySpan<byte> packet)
    {
        int headerSize = 8 + 2 * IntPtr.Size;
        if (packet.Length < headerSize + 24 || BinaryPrimitives.ReadUInt32LittleEndian(packet) != 0) return false;
        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]);
        return declaredSize >= headerSize + 24 && declaredSize <= packet.Length &&
            (BinaryPrimitives.ReadUInt16LittleEndian(packet[(headerSize + 4)..]) & 1) != 0;
    }

    internal static POINT DecodeMessagePosition(uint packed) => new()
    {
        x = unchecked((short)(packed & 0xffff)),
        y = unchecked((short)(packed >> 16))
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(ref RAWINPUTDEVICE device, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint GetRawInputData(IntPtr rawInput, uint command, void* data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern uint GetMessagePos();
}
