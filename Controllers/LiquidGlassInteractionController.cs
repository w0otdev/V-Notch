using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Controllers;

namespace VNotch.Controllers;

public sealed class LiquidGlassInteractionController : IDisposable
{
    private readonly FrameworkElement _eventSource;
    private readonly FrameworkElement _coordinateElement;
    private readonly LiquidGlassRefractionEffect _effect;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _lastTime;

    private double _targetPointerX = 0.5;
    private double _targetPointerY = 0.5;

    private double _pointerX = 0.5;
    private double _pointerY = 0.5;
    private double _pointerVelocityX;
    private double _pointerVelocityY;

    private double _active;
    private double _activeVelocity;
    private double _activeTarget;

    private double _press;
    private double _pressVelocity;
    private double _pressTarget;

    private double _lightX = 0.15;
    private double _lightY = -0.15;
    private double _lightVelocityX;
    private double _lightVelocityY;

    private bool _disposed;

    public LiquidGlassInteractionController(
        FrameworkElement eventSource,
        FrameworkElement coordinateElement,
        LiquidGlassRefractionEffect effect)
    {
        _eventSource = eventSource;
        _coordinateElement = coordinateElement;
        _effect = effect;

        _eventSource.MouseEnter += OnMouseEnter;
        _eventSource.MouseLeave += OnMouseLeave;
        _eventSource.MouseMove += OnMouseMove;

        _eventSource.PreviewMouseLeftButtonDown += OnMouseDown;
        _eventSource.PreviewMouseLeftButtonUp += OnMouseUp;

        CompositionTarget.Rendering += OnRendering;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _activeTarget = 1.0;
        UpdatePointer(e.GetPosition(_coordinateElement));
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _activeTarget = 0.0;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        _activeTarget = 1.0;
        UpdatePointer(e.GetPosition(_coordinateElement));
    }

    private void OnMouseDown(
        object sender,
        MouseButtonEventArgs e)
    {
        UpdatePointer(e.GetPosition(_coordinateElement));

        _activeTarget = 1.0;
        _pressTarget = 1.0;
        _press = 0.7; // Tăng ngay 70% giá trị để hiệu ứng click hiện ra tức thì
    }

    private void OnMouseUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _pressTarget = 0.0;

        _activeTarget =
            _eventSource.IsMouseOver
                ? 1.0
                : 0.0;
    }

    private void UpdatePointer(Point position)
    {
        double width = Math.Max(_coordinateElement.ActualWidth, 1.0);
        double height = Math.Max(_coordinateElement.ActualHeight, 1.0);

        _targetPointerX =
            Math.Clamp(position.X / width, 0.0, 1.0);

        _targetPointerY =
            Math.Clamp(position.Y / height, 0.0, 1.0);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double currentTime = _clock.Elapsed.TotalSeconds;

        double deltaTime =
            Math.Clamp(
                currentTime - _lastTime,
                0.0,
                1.0 / 20.0);

        _lastTime = currentTime;

        // Pointer hơi chậm hơn chuột để tạo cảm giác khối lượng.
        Spring(
            ref _pointerX,
            ref _pointerVelocityX,
            _targetPointerX,
            stiffness: 150.0,
            damping: 18.0,
            deltaTime);

        Spring(
            ref _pointerY,
            ref _pointerVelocityY,
            _targetPointerY,
            stiffness: 150.0,
            damping: 18.0,
            deltaTime);

        Spring(
            ref _active,
            ref _activeVelocity,
            _activeTarget,
            stiffness: 95.0,
            damping: 15.0,
            deltaTime);

        Spring(
            ref _press,
            ref _pressVelocity,
            _pressTarget,
            stiffness: 210.0,
            damping: 18.0,
            deltaTime);

        // Highlight tự di chuyển phía trên vật liệu khi idle.
        // Y âm nghĩa là nguồn sáng nằm bên ngoài notch.
        double idleLightX =
            0.5 +
            Math.Sin(currentTime * 0.48) * 0.62;

        double idleLightY =
            -0.18 +
            Math.Cos(currentTime * 0.31) * 0.10;

        double targetLightX =
            Lerp(
                idleLightX,
                _pointerX,
                _press);

        double targetLightY =
            Lerp(
                idleLightY,
                _pointerY,
                _press);

        Spring(
            ref _lightX,
            ref _lightVelocityX,
            targetLightX,
            stiffness: 75.0,
            damping: 14.0,
            deltaTime);

        Spring(
            ref _lightY,
            ref _lightVelocityY,
            targetLightY,
            stiffness: 75.0,
            damping: 14.0,
            deltaTime);

        _effect.PointerX = _pointerX;
        _effect.PointerY = _pointerY;
        _effect.PointerActive = _active;
        _effect.PressAmount = _press;

        _effect.LightX = _lightX;
        _effect.LightY = _lightY;
    }

    private static void Spring(
        ref double value,
        ref double velocity,
        double target,
        double stiffness,
        double damping,
        double deltaTime)
    {
        if (deltaTime <= 0.0)
            return;

        double acceleration =
            (target - value) *
            stiffness;

        velocity += acceleration * deltaTime;

        velocity *= Math.Exp(
            -damping * deltaTime);

        value += velocity * deltaTime;
    }

    private static double Lerp(
        double from,
        double to,
        double amount)
    {
        return from + (to - from) * amount;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        CompositionTarget.Rendering -= OnRendering;

        _eventSource.MouseEnter -= OnMouseEnter;
        _eventSource.MouseLeave -= OnMouseLeave;
        _eventSource.MouseMove -= OnMouseMove;

        _eventSource.PreviewMouseLeftButtonDown -= OnMouseDown;
        _eventSource.PreviewMouseLeftButtonUp -= OnMouseUp;
    }
}
