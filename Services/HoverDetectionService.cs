using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace VNotch.Services;

public class HoverDetectionService : IDisposable
{
    private const int FastIntervalMs = 33;
    private const int SlowIntervalMs = 125;
    private const int ProximityMargin = 120;
    private const int TopEdgeRevealThickness = 3;

    private readonly DispatcherTimer _pollTimer;
    private readonly int _hoverZoneMargin;
    private Rect _notchBounds;
    private Rect _hoverZone;
    private Rect _proximityZone;
    private bool _isHovering;
    private bool _disposed;

    private DateTime _hoverEnterTime;
    private DateTime _hoverLeaveTime;
    private readonly TimeSpan _enterDelay = TimeSpan.FromMilliseconds(150);
    private readonly TimeSpan _leaveDelay = TimeSpan.FromMilliseconds(400);
    private bool _pendingEnter;
    private bool _pendingLeave;

    public event EventHandler? HoverEnter;
    public event EventHandler? HoverLeave;
    public event EventHandler<Point>? MousePositionChanged;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public bool IsHovering => _isHovering;

    public HoverDetectionService(int hoverZoneMargin = 50)
    {
        _hoverZoneMargin = hoverZoneMargin;
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(SlowIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
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

        _proximityZone = new Rect(
            _hoverZone.Left - ProximityMargin,
            _hoverZone.Top - ProximityMargin,
            _hoverZone.Width + ProximityMargin * 2,
            _hoverZone.Height + ProximityMargin * 2
        );
    }

    public void Start()
    {
        if (!_disposed)
        {
            ApplyInterval(FastIntervalMs);
            _pollTimer.Start();
        }
    }

    public void Stop()
    {
        _pollTimer.Stop();
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out POINT point)) return;

        var mousePoint = new Point(point.X, point.Y);
        MousePositionChanged?.Invoke(this, mousePoint);

        bool isInZone = _hoverZone.Contains(mousePoint);
        var now = DateTime.Now;

        if (isInZone)
        {

            _pendingLeave = false;

            if (!_isHovering)
            {
                if (!_pendingEnter)
                {

                    _pendingEnter = true;
                    _hoverEnterTime = now;
                }
                else if (now - _hoverEnterTime >= _enterDelay)
                {

                    _isHovering = true;
                    _pendingEnter = false;
                    HoverEnter?.Invoke(this, EventArgs.Empty);
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
                }
                else if (now - _hoverLeaveTime >= _leaveDelay)
                {

                    _isHovering = false;
                    _pendingLeave = false;
                    HoverLeave?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        bool needFast = _proximityZone.Contains(mousePoint)
                        || _isHovering || _pendingEnter || _pendingLeave;
        ApplyInterval(needFast ? FastIntervalMs : SlowIntervalMs);
    }

    private void ApplyInterval(int intervalMs)
    {
        if ((int)_pollTimer.Interval.TotalMilliseconds == intervalMs) return;
        _pollTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
    }

    public bool IsPointOverNotch(Point point)
    {
        return _notchBounds.Contains(point);
    }

    public bool IsPointInTopEdgeRevealZone(Point point)
    {
        if (_notchBounds.IsEmpty || _notchBounds.Width <= 0) return false;

        double left = _notchBounds.Left - _hoverZoneMargin;
        double right = _notchBounds.Right + _hoverZoneMargin;
        double bottom = _notchBounds.Top + TopEdgeRevealThickness;

        return point.X >= left && point.X <= right
            && point.Y >= _notchBounds.Top && point.Y <= bottom;
    }

    public bool IsPointInHoverZone(Point point)
    {
        return !_hoverZone.IsEmpty && _hoverZone.Contains(point);
    }

    public double GetDistanceToNotch(Point point)
    {
        var center = new Point(
            _notchBounds.Left + _notchBounds.Width / 2,
            _notchBounds.Top + _notchBounds.Height / 2
        );

        return Math.Sqrt(
            Math.Pow(point.X - center.X, 2) +
            Math.Pow(point.Y - center.Y, 2)
        );
    }

    public void ResetHoverState()
    {
        _isHovering = false;
        _pendingEnter = false;
        _pendingLeave = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _pollTimer.Stop();
            _disposed = true;
        }
    }
}
