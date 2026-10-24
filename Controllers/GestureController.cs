using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace VNotch.Controllers;

public sealed partial class GestureController
{
    private bool _isTracking;
    private Point _startPoint;
    private double _accumulatedX;
    private double _accumulatedY;
    private DateTime _lastSwipeTime = DateTime.MinValue;
    private DateTime _lastTapTime = DateTime.MinValue;
    private bool _gestureTriggered;

    public event Action? SwipeLeft;
    public event Action? SwipeRight;
    public event Action? SwipeDown;
    public event Action? DoubleTap;

    public void BeginTracking(Point position)
    {
        _isTracking = true;
        _startPoint = position;
        _accumulatedX = 0;
        _accumulatedY = 0;
        _gestureTriggered = false;
    }

    public bool UpdateTracking(Point currentPosition)
    {
        if (!_isTracking || _gestureTriggered) return false;

        double elapsedMs = (DateTime.UtcNow - _lastSwipeTime).TotalMilliseconds;
        vn_gesture_evaluate_move(
            _startPoint.X, _startPoint.Y,
            currentPosition.X, currentPosition.Y,
            elapsedMs,
            out _accumulatedX, out _accumulatedY,
            out int gestureKind);

        if (gestureKind == 0) return false;

        _gestureTriggered = true;
        _lastSwipeTime = DateTime.UtcNow;

        switch (gestureKind)
        {
            case 1:
                SwipeLeft?.Invoke();
                break;
            case 2:
                SwipeRight?.Invoke();
                break;
            case 3:
                SwipeDown?.Invoke();
                break;
        }
        return true;
    }

    public bool EndTracking(Point position)
    {
        if (!_isTracking) return false;
        _isTracking = false;

        if (_gestureTriggered) return false;

        var now = DateTime.UtcNow;
        double elapsedTapMs = (now - _lastTapTime).TotalMilliseconds;
        vn_gesture_evaluate_end(
            _startPoint.X, _startPoint.Y,
            position.X, position.Y,
            elapsedTapMs,
            out byte isTap, out byte isDoubleTap);

        if (isTap != 0)
        {
            if (isDoubleTap != 0)
            {
                _lastTapTime = DateTime.MinValue;
                DoubleTap?.Invoke();
                return true;
            }
            _lastTapTime = now;
        }

        return false;
    }

    public void CancelTracking()
    {
        _isTracking = false;
        _gestureTriggered = false;
    }

    public bool IsTracking => _isTracking;

    public bool IsGestureActive { get; set; }

    public bool GestureTriggered => _gestureTriggered;

    public double AccumulatedX => _accumulatedX;

    public double AccumulatedY => _accumulatedY;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_gesture_evaluate_move(
        double start_x,
        double start_y,
        double current_x,
        double current_y,
        double elapsed_since_last_swipe_ms,
        out double out_delta_x,
        out double out_delta_y,
        out int out_gesture_kind);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_gesture_evaluate_end(
        double start_x,
        double start_y,
        double end_x,
        double end_y,
        double elapsed_since_last_tap_ms,
        out byte out_is_tap,
        out byte out_is_double_tap);
}
