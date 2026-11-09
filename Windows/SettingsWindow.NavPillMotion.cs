using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
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

    private NavPillSpringState _navPillState;
    private bool _navPillAnimating;
    private double _navPillTargetY;
    private double _navPillHeight = 30;
    private long _navPillFrameTimestamp;
    private TimeSpan _navPillRenderingTime = TimeSpan.MinValue;

    private void StartNavPillMotion(double targetY, double height, bool animate)
    {
        if (!_navPillAnimating)
        {
            double top = NavActiveIndicator.TranslatePoint(new Point(0, 0), NavItemsStack).Y;
            _navPillState = new NavPillSpringState
            {
                Top = top,
                Bottom = top + NavActiveIndicator.ActualHeight * NavActiveIndicatorScale.ScaleY,
                WidthScale = NavActiveIndicatorScale.ScaleX
            };
        }
        _navPillTargetY = targetY;
        _navPillHeight = height;
        NavActiveIndicator.Height = height;
        NavActiveIndicatorScale.CenterY = 0;
        if (!animate)
        {
            StopNavPillMotion();
            return;
        }
        // Retargeting keeps both edge velocities and the current shape intact.
        if (_navPillAnimating) return;
        _navPillAnimating = true;
        _navPillFrameTimestamp = Stopwatch.GetTimestamp();
        _navPillRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += RenderNavPillMotion;
    }

    private void RenderNavPillMotion(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _navPillRenderingTime) return;
            _navPillRenderingTime = frame.RenderingTime;
        }
        if (!IsVisible || _isClosing || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            StopNavPillMotion();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_navPillFrameTimestamp, now).TotalSeconds;
        if (seconds < 0.9 / AnimationConfig.TargetFps) return;
        _navPillFrameTimestamp = now;
        AdvanceNavPillMotion(seconds);
    }

    private void AdvanceNavPillMotion(double seconds)
    {
        if (!_navPillAnimating) return;
        try
        {
            if (NativeStepNavPill(ref _navPillState, _navPillTargetY, _navPillHeight, seconds, 0,
                    out var visual, out byte settled) != 0)
            {
                StopNavPillMotion();
                return;
            }
            NavActiveIndicatorTranslate.Y = visual.TranslateY;
            NavActiveIndicatorScale.ScaleX = visual.ScaleX;
            NavActiveIndicatorScale.ScaleY = visual.ScaleY;
            if (settled != 0) StopNavPillMotion();
        }
        catch (DllNotFoundException) { StopNavPillMotion(); }
        catch (EntryPointNotFoundException) { StopNavPillMotion(); }
    }

    private void StopNavPillMotion()
    {
        CompositionTarget.Rendering -= RenderNavPillMotion;
        _navPillAnimating = false;
        _navPillState = new NavPillSpringState
        {
            Top = _navPillTargetY, Bottom = _navPillTargetY + _navPillHeight, WidthScale = 1
        };
        NavActiveIndicatorTranslate.Y = _navPillTargetY;
        NavActiveIndicatorScale.ScaleX = NavActiveIndicatorScale.ScaleY = 1;
        NavActiveIndicatorScale.CenterY = 0;
    }

    private void NavPillVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) StopNavPillMotion();
    }
}
