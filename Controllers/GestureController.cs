using System;
using System.Windows;

namespace VNotch.Controllers;

public sealed class GestureController
{
    private const double HorizontalSwipeThreshold = 40.0;
    private const double VerticalSwipeThreshold = 35.0;
    private const double DirectionLockRatio = 1.6;
    private const int DoubleTapMaxMs = 350;
    private const int SwipeCooldownMs = 400;
    private const double DeadZone = 4.0;

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

        double deltaX = currentPosition.X - _startPoint.X;
        double deltaY = currentPosition.Y - _startPoint.Y;

        _accumulatedX = deltaX;
        _accumulatedY = deltaY;

        double absX = Math.Abs(_accumulatedX);
        double absY = Math.Abs(_accumulatedY);

        if (absX < DeadZone && absY < DeadZone) return false;

        if ((DateTime.UtcNow - _lastSwipeTime).TotalMilliseconds < SwipeCooldownMs) return false;

        if (absX >= HorizontalSwipeThreshold && absX > absY * DirectionLockRatio)
        {
            _gestureTriggered = true;
            _lastSwipeTime = DateTime.UtcNow;

            if (_accumulatedX < 0)
                SwipeLeft?.Invoke();
            else
                SwipeRight?.Invoke();

            return true;
        }

        if (absY >= VerticalSwipeThreshold && _accumulatedY > 0 && absY > absX * DirectionLockRatio)
        {
            _gestureTriggered = true;
            _lastSwipeTime = DateTime.UtcNow;
            SwipeDown?.Invoke();
            return true;
        }

        return false;
    }

    public bool EndTracking(Point position)
    {
        if (!_isTracking) return false;
        _isTracking = false;

        if (_gestureTriggered) return false;

        double totalMovement = Math.Abs(position.X - _startPoint.X) + Math.Abs(position.Y - _startPoint.Y);
        if (totalMovement < DeadZone * 2)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastTapTime).TotalMilliseconds <= DoubleTapMaxMs)
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
}
