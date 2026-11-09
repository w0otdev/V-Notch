using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NavHoverSpringState
    {
        public double Y, YVelocity, Opacity, OpacityVelocity, SelectedOpacity, SelectedVelocity;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_nav_hover")]
    private static partial int NativeStepNavHover(ref NavHoverSpringState state, double targetY,
        byte visible, byte selected, double seconds, byte reducedMotion, out byte settled);

    private NavHoverSpringState _navHoverState;
    private double _navHoverTargetY;
    private bool _navHoverVisible, _navHoverSelected, _navHoverAnimating, _navHoverInitialized;
    private long _navHoverFrameTimestamp;
    private TimeSpan _navHoverRenderingTime = TimeSpan.MinValue;

    private void StartNavHoverMotion(double targetY, bool visible, bool selected, bool animate)
    {
        // Only place a completely hidden, resting surface directly at a new row.
        // Every in-flight retarget keeps its position, opacity and velocity.
        if (!_navHoverInitialized || (!_navHoverAnimating && _navHoverState.Opacity == 0 && visible))
        {
            _navHoverState.Y = targetY;
            _navHoverState.YVelocity = 0;
            _navHoverInitialized = true;
        }
        _navHoverTargetY = targetY;
        _navHoverVisible = visible;
        _navHoverSelected = selected;
        if (!animate)
        {
            StopNavHoverMotion();
            return;
        }
        if (_navHoverAnimating) return;
        _navHoverAnimating = true;
        _navHoverFrameTimestamp = Stopwatch.GetTimestamp();
        _navHoverRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += RenderNavHoverMotion;
    }

    private void RenderNavHoverMotion(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _navHoverRenderingTime) return;
            _navHoverRenderingTime = frame.RenderingTime;
        }
        if (!IsVisible || _isClosing || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            StopNavHoverMotion();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_navHoverFrameTimestamp, now).TotalSeconds;
        if (seconds < .9 / AnimationConfig.TargetFps) return;
        _navHoverFrameTimestamp = now;
        AdvanceNavHoverMotion(seconds);
    }

    private void AdvanceNavHoverMotion(double seconds)
    {
        if (!_navHoverAnimating) return;
        try
        {
            if (NativeStepNavHover(ref _navHoverState, _navHoverTargetY, _navHoverVisible ? (byte)1 : (byte)0,
                    _navHoverSelected ? (byte)1 : (byte)0, seconds, 0, out byte settled) != 0 || settled != 0)
            {
                StopNavHoverMotion();
                return;
            }
            ApplyNavHoverVisual();
        }
        catch (DllNotFoundException) { StopNavHoverMotion(); }
        catch (EntryPointNotFoundException) { StopNavHoverMotion(); }
    }

    private void ApplyNavHoverVisual()
    {
        NavHoverIndicatorTranslate.Y = _navHoverState.Y;
        NavHoverIndicator.Opacity = Math.Clamp(_navHoverState.Opacity, 0, 1);
        NavActiveHoverOverlay.Opacity = .12 * Math.Clamp(_navHoverState.SelectedOpacity, 0, 1);
    }

    private void StopNavHoverMotion()
    {
        CompositionTarget.Rendering -= RenderNavHoverMotion;
        _navHoverAnimating = false;
        _navHoverState = new NavHoverSpringState
        {
            Y = _navHoverTargetY, Opacity = _navHoverVisible ? 1 : 0, SelectedOpacity = _navHoverSelected ? 1 : 0
        };
        ApplyNavHoverVisual();
    }
}
