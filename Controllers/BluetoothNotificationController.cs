using System;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class BluetoothNotificationController
{
    private readonly object _stateGate = new();
    private bool _isVisible;
    private bool _currentConnected = true;
    private System.Timers.Timer? _dismissTimer;
    private long _dismissGeneration;

    public bool IsVisible
    {
        get
        {
            lock (_stateGate)
                return _isVisible;
        }
    }

    public bool CurrentConnected
    {
        get
        {
            lock (_stateGate)
                return _currentConnected;
        }
    }

    public event Action<BluetoothDeviceInfo, bool>? ShowRequested;

    public event Action? DismissRequested;

    public double AutoDismissMs { get; set; } = 3000;

    public bool TryShow(BluetoothDeviceInfo device, bool connected,
        bool isNotchVisible, bool isAnimating, bool isExpanded, bool isMusicExpanded)
    {
        if (!isNotchVisible || isAnimating || isExpanded || isMusicExpanded)
            return false;

        lock (_stateGate)
        {
            _currentConnected = connected;
            _isVisible = true;
            StartDismissTimerLocked();

            // Keep event ordering serialized with the timer callback: a stale
            // dismiss must never be queued after this newer show request.
            ShowRequested?.Invoke(device, connected);
        }

        return true;
    }

    public void MarkDismissed()
    {
        lock (_stateGate)
        {
            _isVisible = false;
            StopDismissTimerLocked();
        }
    }

    public void ForceDismiss()
    {
        lock (_stateGate)
        {
            if (!_isVisible) return;

            StopDismissTimerLocked();
            _isVisible = false;
            DismissRequested?.Invoke();
        }
    }

    public static Geometry GetIconGeometry(BluetoothDeviceType deviceType)
    {
        return deviceType switch
        {
            BluetoothDeviceType.Headphones => Geometry.Parse(
                "M12,1C7,1,3,5,3,10v6c0,1.66,1.34,3,3,3h1c1.1,0,2-0.9,2-2v-4c0-1.1-0.9-2-2-2H5.17C5.6,6.17,8.48,3.5,12,3.5 s6.4,2.67,6.83,6.5H17c-1.1,0-2,0.9-2,2v4c0,1.1,0.9,2,2,2h1c1.66,0,3-1.34,3-3v-6C21,5,17,1,12,1z"),
            BluetoothDeviceType.Speaker => Geometry.Parse(
                "M3,9v6h4l5,5V4L7,9H3z M16.5,12c0-1.77-1.02-3.29-2.5-4.03v8.05C15.48,15.29,16.5,13.77,16.5,12z M14,3.23v2.06 c2.89,0.86,5,3.54,5,6.71s-2.11,5.85-5,6.71v2.06c4.01-0.91,7-4.49,7-8.77S18.01,4.14,14,3.23z"),
            BluetoothDeviceType.Keyboard => Geometry.Parse(
                "M20,5H4C2.9,5,2,5.9,2,7v10c0,1.1,0.9,2,2,2h16c1.1,0,2-0.9,2-2V7C22,5.9,21.1,5,20,5z M11,8h2v2h-2V8z M11,11h2v2h-2 V11z M8,8h2v2H8V8z M8,11h2v2H8V11z M7,13H5v-2h2V13z M7,10H5V8h2V10z M16,17H8v-2h8V17z M16,13h-2v-2h2V13z M16,10h-2V8h2V10z M19,13h-2v-2h2V13z M19,10h-2V8h2V10z"),
            BluetoothDeviceType.Mouse => Geometry.Parse(
                "M13,1.07V9h7c0-4.08-3.05-7.44-7-7.93z M4,15c0,4.42,3.58,8,8,8s8-3.58,8-8v-4H4V15z M11,1.07C7.05,1.56,4,4.92,4,9h7V1.07z"),
            BluetoothDeviceType.GameController => Geometry.Parse(
                "M21.58,16.09l-1.09-7.66C20.21,6.46,18.52,5,16.53,5H7.47C5.48,5,3.79,6.46,3.51,8.43l-1.09,7.66 C2.2,17.63,3.39,19,4.94,19c0.68,0,1.32-0.27,1.8-0.75L9,16h6l2.25,2.25c0.48,0.48,1.13,0.75,1.8,0.75 C20.61,19,21.8,17.63,21.58,16.09z M11,11H9v2H8v-2H6v-1h2V8h1v2h2V11z M15,10c-0.55,0-1-0.45-1-1s0.45-1,1-1s1,0.45,1,1 S15.55,10,15,10z M17,13c-0.55,0-1-0.45-1-1s0.45-1,1-1s1,0.45,1,1S17.55,13,17,13z"),
            BluetoothDeviceType.Phone => Geometry.Parse(
                "M17,1.01L7,1C5.9,1,5,1.9,5,3v18c0,1.1,0.9,2,2,2h10c1.1,0,2-0.9,2-2V3C19,1.9,18.1,1.01,17,1.01z M17,19H7V5h10V19z"),
            _ => Geometry.Parse(
                "M17.71,7.71L12,2h-1v7.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L11,14.41V22h1l5.71-5.71L13.41,12L17.71,7.71z M13,5.83l1.88,1.88L13,9.59V5.83z M14.88,16.29L13,18.17v-3.76L14.88,16.29z")
        };
    }

    private void StartDismissTimerLocked()
    {
        StopDismissTimerLocked();

        long generation = ++_dismissGeneration;
        var timer = new System.Timers.Timer(AutoDismissMs)
        {
            AutoReset = false
        };
        _dismissTimer = timer;

        timer.Elapsed += (s, e) =>
        {
            lock (_stateGate)
            {
                if (generation != _dismissGeneration ||
                    !ReferenceEquals(_dismissTimer, timer) ||
                    !_isVisible)
                {
                    return;
                }

                _dismissTimer = null;
                ++_dismissGeneration;
                _isVisible = false;
                timer.Dispose();

                // Invoke while serialized so a newer ShowRequested cannot be
                // queued ahead of this dismissal and then closed by it.
                DismissRequested?.Invoke();
            }
        };
        timer.Start();
    }

    private void StopDismissTimerLocked()
    {
        ++_dismissGeneration;
        var timer = _dismissTimer;
        _dismissTimer = null;
        timer?.Stop();
        timer?.Dispose();
    }
}
