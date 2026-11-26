using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using VNotch.Interop;

namespace VNotch.Services;

public static partial class InputMonitorService
{
    private const string NativeDll = NativeAbi.LibraryName;
    private const int WM_INPUT = 0x00FF;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(2);

#pragma warning disable S101 // Win32 POINT struct name
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
#pragma warning restore S101
    {
        public int x;
        public int y;
    }

    [LibraryImport(NativeDll)]
    private static partial int vn_input_monitor_is_left_button_down(
        ReadOnlySpan<byte> packetPtr,
        int packetLen,
        int ptrSize,
        out byte outIsDown);

    [LibraryImport(NativeDll)]
    private static partial int vn_input_register_raw_mouse(long hwnd, byte remove, out int lastError);

    [LibraryImport(NativeDll)]
    private static partial int vn_input_read_left_click(long rawInput, out byte leftDown, out int x, out int y);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageTime();

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_input_monitor_decode_message_position(
        uint packed,
        out int outX,
        out int outY);

    private static HwndSource? _source;
    private static HwndSource? _primarySource;
    private static readonly List<Subscription> Subscriptions = [];
    private static Dispatcher? _inputDispatcher;
    private static HwndSource? _inputWindow;
    private static int _generation;
    internal static bool IsStarted => _source != null;
    internal static Dispatcher? InputDispatcher => _inputDispatcher;
    internal static Action? InitializeWorkerThread { private get; set; }

    public static event EventHandler<POINT>? MouseActionTriggered;

    public static void Start(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return;
        var source = HwndSource.FromHwnd(windowHandle);
        if (source == null || source.IsDisposed) return;
        source.Dispatcher.VerifyAccess();
        _primarySource = source;
        UpdateSource();
    }

    // Independent consumers keep the one process-wide raw-input registration
    // alive without taking ownership away from the notch's Start/Stop calls.
    internal static IDisposable Subscribe(IntPtr windowHandle, Action<POINT, uint> onPress)
    {
        var source = HwndSource.FromHwnd(windowHandle);
        if (source == null || source.IsDisposed) throw new InvalidOperationException("A live window is required.");
        source.Dispatcher.VerifyAccess();
        var subscription = new Subscription(source, onPress);
        Subscriptions.Add(subscription);
        UpdateSource();
        return subscription;
    }

    private static void UpdateSource()
    {
        var source = _primarySource is { IsDisposed: false } ? _primarySource
            : Subscriptions.Select(subscription => subscription.Source).FirstOrDefault(candidate => !candidate.IsDisposed);
        if (ReferenceEquals(source, _source) && (source == null || _inputDispatcher != null)) return;
        StopCore();
        if (source != null) StartCore(source);
    }

    private static void StartCore(HwndSource source)
    {
        int generation = ++_generation;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                InitializeWorkerThread?.Invoke();
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var input = new HwndSource(new HwndSourceParameters("VNotch input worker")
                { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
                input.AddHook((IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (message == WM_INPUT &&
                        vn_input_read_left_click(lp, out byte down, out int x, out int y) == 0 && down != 0 &&
                        !source.Dispatcher.HasShutdownStarted)
                    {
                        // Movement and wheel packets never wake the WPF UI dispatcher.
                        uint timestamp = unchecked((uint)GetMessageTime());
                        source.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            if (generation == _generation && ReferenceEquals(_source, source))
                                DispatchClick(new POINT { x = x, y = y }, timestamp);
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
                ready.TrySetResult(true);
                try { Dispatcher.Run(); }
                finally
                {
                    if (!dispatcher.HasShutdownStarted) dispatcher.InvokeShutdown();
                    Interlocked.CompareExchange(ref _inputDispatcher, null, dispatcher);
                    Interlocked.CompareExchange(ref _inputWindow, null, input);
                }
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
        if (ready.Task.Wait(WorkerTimeout) && ready.Task.Result)
            _source = source;
        else
            RuntimeLog.Warn("INPUT", "Timed out waiting for raw mouse input worker.");
    }

    public static void Stop()
    {
        _primarySource?.Dispatcher.VerifyAccess();
        _primarySource = null;
        UpdateSource();
    }

    private static void StopCore()
    {
        var source = _source;
        if (source == null) return;
        source.Dispatcher.VerifyAccess();
        ++_generation;
        _source = null;
        var dispatcher = _inputDispatcher;
        _inputDispatcher = null;
        if (dispatcher == null || !dispatcher.Thread.IsAlive || dispatcher.HasShutdownStarted) return;
        // Unregister before another Start can register its window; bound the wait so a stalled worker cannot wedge shutdown.
        try
        {
            bool completed = dispatcher.InvokeAsync(() =>
            {
                vn_input_register_raw_mouse(0, 1, out _);
                _inputWindow?.Dispose();
                _inputWindow = null;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }, DispatcherPriority.Send).Task.Wait(WorkerTimeout);

            if (!completed)
                RuntimeLog.Warn("INPUT", "Timed out waiting for raw mouse input worker shutdown.");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is OperationCanceledException))
        {
            // Worker dispatcher already shut down concurrently.
        }
    }

    internal static void DispatchClick(POINT point, uint timestamp)
    {
        // A handler may show or hide another window; deliver only to consumers
        // that existed when this click was queued, never to a newly opened popup.
        var listeners = Subscriptions.ToArray();
        MouseActionTriggered?.Invoke(null, point);
        foreach (var listener in listeners)
            if (!listener.IsDisposed && unchecked((int)(timestamp - listener.StartedAt)) > 0)
                listener.OnPress(point, timestamp);
    }

    private sealed class Subscription : IDisposable
    {
        internal HwndSource Source { get; }
        internal Action<POINT, uint> OnPress { get; }
        internal uint StartedAt { get; } = unchecked((uint)Environment.TickCount);
        internal bool IsDisposed { get; private set; }

        internal Subscription(HwndSource source, Action<POINT, uint> onPress)
        {
            Source = source;
            OnPress = onPress;
            source.Disposed += SourceDisposed;
        }

        private void SourceDisposed(object? sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            Source.Dispatcher.VerifyAccess();
            if (IsDisposed) return;
            IsDisposed = true;
            Source.Disposed -= SourceDisposed;
            Subscriptions.Remove(this);
            UpdateSource();
        }
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
