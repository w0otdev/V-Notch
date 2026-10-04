using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controllers;

public sealed class LiquidGlassInteractionController : IDisposable
{
    private readonly FrameworkElement _eventSource;
    private readonly FrameworkElement _coordinateElement;
    private readonly LiquidGlassRefractionEffect _effect;
    private readonly LiquidGlassInteractionState _state = new();
    private readonly Stopwatch _clock = new();
    private bool _hooked;
    private bool _disposed;

    internal bool IsRenderingSubscribed => _hooked;

    public LiquidGlassInteractionController(FrameworkElement eventSource,
        FrameworkElement coordinateElement, LiquidGlassRefractionEffect effect)
    {
        _eventSource = eventSource;
        _coordinateElement = coordinateElement;
        _effect = effect;
        _eventSource.MouseEnter += OnMouseMove;
        _eventSource.MouseLeave += OnMouseLeave;
        _eventSource.MouseMove += OnMouseMove;
        _eventSource.PreviewMouseLeftButtonDown += OnMouseDown;
        _eventSource.PreviewMouseLeftButtonUp += OnMouseUp;
        _eventSource.IsVisibleChanged += OnVisibilityChanged;
        _coordinateElement.IsVisibleChanged += OnVisibilityChanged;
        AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
        ApplyFrame();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _state.SetActive(false);
        _state.SetPressed(false);
        RequestRendering();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        UpdatePointer(e.GetPosition(_coordinateElement));
        RequestRendering();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        UpdatePointer(e.GetPosition(_coordinateElement));
        _state.SetPressed(true);
        RequestRendering();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _state.SetPressed(false);
        _state.SetActive(_eventSource.IsMouseOver);
        RequestRendering();
    }

    private void UpdatePointer(Point position) => _state.MovePointer(
        position.X / Math.Max(_coordinateElement.ActualWidth, 1),
        position.Y / Math.Max(_coordinateElement.ActualHeight, 1));

    private void RequestRendering()
    {
        if (_disposed) return;
        if (AnimationConfig.ReduceMotion)
        {
            _state.SnapToTargets();
            ApplyFrame();
            StopRendering();
            return;
        }
        if (_hooked || _state.IsSettled || !_eventSource.IsVisible || !_coordinateElement.IsVisible) return;
        _hooked = true;
        _clock.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_eventSource.IsVisible || !_coordinateElement.IsVisible)
        {
            StopRendering();
            return;
        }
        double deltaTime = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        _state.Advance(deltaTime);
        ApplyFrame();
        if (_state.IsSettled) StopRendering();
    }

    private void ApplyFrame()
    {
        var frame = _state.Frame;
        if (_effect.PointerX != frame.PointerX) _effect.PointerX = frame.PointerX;
        if (_effect.PointerY != frame.PointerY) _effect.PointerY = frame.PointerY;
        if (_effect.PointerActive != frame.Active) _effect.PointerActive = frame.Active;
        if (_effect.PressAmount != frame.Press) _effect.PressAmount = frame.Press;
        if (_effect.LightX != frame.LightX) _effect.LightX = frame.LightX;
        if (_effect.LightY != frame.LightY) _effect.LightY = frame.LightY;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_eventSource.IsVisible || !_coordinateElement.IsVisible)
        {
            _state.SetActive(false);
            _state.SetPressed(false);
            _state.SnapToTargets();
            StopRendering();
            ApplyFrame();
        }
        else RequestRendering();
    }

    private void OnReduceMotionChanged() => RequestRendering();

    private void StopRendering()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRendering();
        _eventSource.MouseEnter -= OnMouseMove;
        _eventSource.MouseLeave -= OnMouseLeave;
        _eventSource.MouseMove -= OnMouseMove;
        _eventSource.PreviewMouseLeftButtonDown -= OnMouseDown;
        _eventSource.PreviewMouseLeftButtonUp -= OnMouseUp;
        _eventSource.IsVisibleChanged -= OnVisibilityChanged;
        _coordinateElement.IsVisibleChanged -= OnVisibilityChanged;
        AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
    }
}
