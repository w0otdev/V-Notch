using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace VNotch.Services;

public sealed partial class HoverDetectionService : IDisposable
{
    private const int OutsideWindowIntervalMs = 125;

    private readonly UiScheduledTimer _pollTimer;
    private readonly UiScheduledTimer _hoverDelayTimer;
    private readonly Window? _inputWindow;
    private readonly MouseEventHandler _mousePositionHandler;
    private readonly int _hoverZoneMargin;
    private Rect _notchBounds;
    private Rect _hoverZone;
    private bool _isHovering;
    private bool _disposed;

    private long _hoverEnterTime;
    private long _hoverLeaveTime;
    private readonly TimeSpan _enterDelay = TimeSpan.FromMilliseconds(150);
    private readonly TimeSpan _leaveDelay = TimeSpan.FromMilliseconds(400);
    private bool _pendingEnter;
    private bool _pendingLeave;
    private int _lastPolledX = int.MinValue;
    private int _lastPolledY = int.MinValue;

    public event EventHandler? HoverEnter;
    public event EventHandler? HoverLeave;
    public event EventHandler<Point>? MousePositionChanged;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_hover_is_in_top_edge_reveal_zone(
        double notchLeft, double notchTop, double notchWidth, double notchHeight,
        int hoverZoneMargin, double px, double py,
        out byte outInZone);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_hover_get_distance_to_notch(
        double notchLeft, double notchTop, double notchWidth, double notchHeight,
        double px, double py,
        out double outDist);

    public bool IsHovering => _isHovering;

    public HoverDetectionService(int hoverZoneMargin = 50, Window? inputWindow = null)
    {
        _hoverZoneMargin = hoverZoneMargin;
        _inputWindow = inputWindow;
        _mousePositionHandler = InputWindow_MousePositionChanged;
        var dispatcher = inputWindow?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        // WPF cannot receive mouse events outside its HWND, including the reveal
        // strip when the notch is behind other windows. Sample that area slowly.
        _pollTimer = new UiScheduledTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(OutsideWindowIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _hoverDelayTimer = new UiScheduledTimer(DispatcherPriority.Background, dispatcher);
        _hoverDelayTimer.Tick += HoverDelayTimer_Tick;
        if (_inputWindow != null)
        {
            _inputWindow.AddHandler(Mouse.MouseMoveEvent, _mousePositionHandler, handledEventsToo: true);
            _inputWindow.AddHandler(Mouse.MouseEnterEvent, _mousePositionHandler, handledEventsToo: true);
            _inputWindow.AddHandler(Mouse.MouseLeaveEvent, _mousePositionHandler, handledEventsToo: true);
        }
    }

    public void UpdateNotchBounds(double left, double top, double width, double height)
    {
        _notchBounds = new Rect(left, top, width, height);

        _hoverZone = new Rect(
            left - _hoverZoneMargin,
            top,
            width + _hoverZoneMargin * 2,
            height + _hoverZoneMargin
        );
        _lastPolledX = int.MinValue;
        _lastPolledY = int.MinValue;
    }

    public void Start()
    {
        if (!_disposed && !_pollTimer.IsEnabled)
        {
            _lastPolledX = int.MinValue;
            _lastPolledY = int.MinValue;
            _pollTimer.Start();
            PollTimer_Tick(this, EventArgs.Empty);
        }
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _hoverDelayTimer.Stop();
        _pendingEnter = false;
        _pendingLeave = false;
        _lastPolledX = int.MinValue;
        _lastPolledY = int.MinValue;
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!Win32Interop.GetCursorPos(out Win32Interop.POINT point)) return;

        if (!_pendingEnter && !_pendingLeave && point.X == _lastPolledX && point.Y == _lastPolledY)
            return;

        _lastPolledX = point.X;
        _lastPolledY = point.Y;
        ProcessMousePosition(new Point(point.X, point.Y), Stopwatch.GetTimestamp());
    }

    private void InputWindow_MousePositionChanged(object sender, MouseEventArgs e)
    {
        if (_inputWindow == null || !_inputWindow.IsLoaded || !_pollTimer.IsEnabled) return;

        Point screenPt = _inputWindow.PointToScreen(e.GetPosition(_inputWindow));
        _lastPolledX = (int)screenPt.X;
        _lastPolledY = (int)screenPt.Y;
        ProcessMousePosition(screenPt, Stopwatch.GetTimestamp());
    }

    private void HoverDelayTimer_Tick(object? sender, EventArgs e)
    {
        _hoverDelayTimer.Stop();
        _lastPolledX = int.MinValue;
        _lastPolledY = int.MinValue;
        // Recheck at the deadline even if the cursor stopped moving.
        PollTimer_Tick(sender, e);
    }

    internal void ProcessMousePosition(Point mousePoint, long now)
    {
        if (_disposed || !_pollTimer.IsEnabled) return;
        MousePositionChanged?.Invoke(this, mousePoint);
        if (_disposed || !_pollTimer.IsEnabled) return;

        bool isInZone = _hoverZone.Contains(mousePoint);

        if (isInZone)
        {
            _pendingLeave = false;

            if (!_isHovering)
            {
                if (!_pendingEnter)
                {
                    _pendingEnter = true;
                    _hoverEnterTime = now;
                    _hoverDelayTimer.Interval = _enterDelay;
                    _hoverDelayTimer.Start();
                }
                else
                {
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(_hoverEnterTime, now);
                    if (elapsed >= _enterDelay)
                    {
                        _isHovering = true;
                        _pendingEnter = false;
                        HoverEnter?.Invoke(this, EventArgs.Empty);
                    }
                    else if (!_hoverDelayTimer.IsEnabled)
                    {
                        _hoverDelayTimer.Interval = _enterDelay - elapsed;
                        _hoverDelayTimer.Start();
                    }
                }
            }
        }
        else
        {
            _pendingEnter = false;

            if (_isHovering)
            {
                if (!_pendingLeave)
                {
                    _pendingLeave = true;
                    _hoverLeaveTime = now;
                    _hoverDelayTimer.Interval = _leaveDelay;
                    _hoverDelayTimer.Start();
                }
                else
                {
                    TimeSpan elapsed = Stopwatch.GetElapsedTime(_hoverLeaveTime, now);
                    if (elapsed >= _leaveDelay)
                    {
                        _isHovering = false;
                        _pendingLeave = false;
                        HoverLeave?.Invoke(this, EventArgs.Empty);
                    }
                    else if (!_hoverDelayTimer.IsEnabled)
                    {
                        _hoverDelayTimer.Interval = _leaveDelay - elapsed;
                        _hoverDelayTimer.Start();
                    }
                }
            }
        }

        if (!_pendingEnter && !_pendingLeave && _hoverDelayTimer.IsEnabled) _hoverDelayTimer.Stop();
    }

    public bool IsPointOverNotch(Point point)
    {
        return _notchBounds.Contains(point);
    }

    public bool IsPointInTopEdgeRevealZone(Point point)
    {
        if (_notchBounds.IsEmpty || _notchBounds.Width <= 0) return false;
        return vn_hover_is_in_top_edge_reveal_zone(
            _notchBounds.Left, _notchBounds.Top, _notchBounds.Width, _notchBounds.Height,
            _hoverZoneMargin, point.X, point.Y, out byte inZone) == 0 && inZone != 0;
    }

    public bool IsPointInHoverZone(Point point)
    {
        return !_hoverZone.IsEmpty && _hoverZone.Contains(point);
    }

    public double GetDistanceToNotch(Point point)
    {
        vn_hover_get_distance_to_notch(
            _notchBounds.Left, _notchBounds.Top, _notchBounds.Width, _notchBounds.Height,
            point.X, point.Y, out double dist);
        return dist;
    }

    public void ResetHoverState()
    {
        _isHovering = false;
        _pendingEnter = false;
        _pendingLeave = false;
        _lastPolledX = int.MinValue;
        _lastPolledY = int.MinValue;
        _hoverDelayTimer.Stop();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            if (_inputWindow != null)
            {
                _inputWindow.RemoveHandler(Mouse.MouseMoveEvent, _mousePositionHandler);
                _inputWindow.RemoveHandler(Mouse.MouseEnterEvent, _mousePositionHandler);
                _inputWindow.RemoveHandler(Mouse.MouseLeaveEvent, _mousePositionHandler);
            }
            _pollTimer.Dispose();
            _hoverDelayTimer.Dispose();
            _disposed = true;
        }
    }
}
