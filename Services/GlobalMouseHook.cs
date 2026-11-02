using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

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

    private static HwndSource? _source;
    private static Dispatcher? _inputDispatcher;
    private static HwndSource? _inputWindow;
    private static int _generation;
    internal static bool IsStarted => _source != null;
    internal static int InputThreadId { get; private set; }

    public static event EventHandler<POINT>? MouseActionTriggered;

    public static void Start(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(windowHandle);
        if (source == null || source.IsDisposed || ReferenceEquals(source, _source)) return;
        source.Dispatcher.VerifyAccess();
        Stop();
        int generation = ++_generation;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                using var input = new HwndSource(new HwndSourceParameters("VNotch input worker")
                { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
                input.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (message == WM_INPUT &&
                        vn_input_read_left_click(lp, out byte down, out int x, out int y) == 0 && down != 0 &&
                        !source.Dispatcher.HasShutdownStarted)
                    {
                        // Movement and wheel packets never wake the WPF UI dispatcher.
                        source.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            if (generation == _generation && ReferenceEquals(_source, source))
                                MouseActionTriggered?.Invoke(null, new POINT { x = x, y = y });
                        });
                    }
                    return IntPtr.Zero; // DefWindowProc still owns raw-input cleanup.
                });
                if (vn_input_register_raw_mouse(input.Handle, 0, out int error) != 0)
                {
                    RuntimeLog.Warn("INPUT", $"Raw mouse registration failed: {error}");
                    ready.TrySetResult(false);
                    return;
                }
                _inputDispatcher = dispatcher;
                _inputWindow = input;
                InputThreadId = Environment.CurrentManagedThreadId;
                ready.TrySetResult(true);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetResult(false);
                RuntimeLog.Warn("INPUT", $"Input worker stopped: {ex.GetType().Name}");
            }
        })
        { IsBackground = true, Name = "VNotch raw mouse" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (ready.Task.GetAwaiter().GetResult()) _source = source;
    }

    public static void Stop()
    {
        var source = _source;
        if (source == null) return;
        source.Dispatcher.VerifyAccess();
        ++_generation;
        _source = null;
        var dispatcher = _inputDispatcher;
        _inputDispatcher = null;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        // Unregister before another Start can register its window. No thread join
        // or hardware callback is needed on the WPF dispatcher.
        dispatcher.Invoke(() =>
        {
            vn_input_register_raw_mouse(0, 1, out _);
            _inputWindow?.Dispose();
            _inputWindow = null;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }, DispatcherPriority.Send);
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
