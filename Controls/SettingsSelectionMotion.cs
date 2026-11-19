using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using VNotch.Services;

namespace VNotch.Controls;

internal sealed partial class SettingsSelectionMotion(Border indicator, FrameworkElement track,
    ScaleTransform scale, TranslateTransform offset)
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NavPillSpringState
    {
        public double Top, Bottom, TopVelocity, BottomVelocity, WidthScale, WidthVelocity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NavPillVisual
    {
        public double TranslateY, ScaleX, ScaleY;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_nav_pill")]
    private static partial int NativeStepNavPill(ref NavPillSpringState state, double targetY,
        double height, double seconds, byte reducedMotion, out NavPillVisual visual, out byte settled);

    private NavPillSpringState _state;
    internal (double TopVelocity, double BottomVelocity) Velocity => (_state.TopVelocity, _state.BottomVelocity);
    internal bool IsMoving { get; private set; }
    private double _targetY;
    private double _height = 30;
    private AnimationFrameClock _clock;
    private TimeSpan _renderingTime = TimeSpan.MinValue;

    internal void MoveTo(double targetY, double height, bool animate)
    {
        if (!IsMoving)
        {
            double top = indicator.TranslatePoint(new Point(0, 0), track).Y;
            _state = new NavPillSpringState
            {
                Top = top,
                Bottom = top + indicator.ActualHeight * scale.ScaleY,
                WidthScale = scale.ScaleX
            };
        }
        bool resized = height != _height;
        _targetY = targetY;
        _height = height;
        indicator.Height = height;
        scale.CenterY = 0;
        if (!animate || !indicator.IsVisible || AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            Settle();
            return;
        }
        // Retargeting keeps both edge velocities and the current shape intact.
        if (!IsMoving)
        {
            IsMoving = true;
            _clock.Reset(Stopwatch.GetTimestamp());
            _renderingTime = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnRendering;
        }
        if (resized) Advance(0); // Keep the live edges in place when the new row has a different height.
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _renderingTime) return;
            _renderingTime = frame.RenderingTime;
        }
        if (!indicator.IsVisible || AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            Settle();
            return;
        }
        if (!_clock.TryAdvance(Stopwatch.GetTimestamp(), AnimationConfig.TargetFps, out double seconds)) return;
        Advance(seconds);
    }

    internal void Advance(double seconds)
    {
        if (!IsMoving) return;
        try
        {
            if (NativeStepNavPill(ref _state, _targetY, _height, seconds, 0,
                    out var visual, out byte settled) != 0)
            {
                Settle();
                return;
            }
            offset.Y = visual.TranslateY;
            scale.ScaleX = visual.ScaleX;
            scale.ScaleY = visual.ScaleY;
            if (settled != 0) Settle();
        }
        catch (DllNotFoundException) { Settle(); }
        catch (EntryPointNotFoundException) { Settle(); }
    }

    internal void Settle()
    {
        CompositionTarget.Rendering -= OnRendering;
        IsMoving = false;
        _state = new NavPillSpringState
        {
            Top = _targetY, Bottom = _targetY + _height, WidthScale = 1
        };
        offset.Y = _targetY;
        scale.ScaleX = scale.ScaleY = 1;
        scale.CenterY = 0;
    }

}
