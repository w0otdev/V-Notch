using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

internal sealed partial class MenuSurfaceSpring
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct State
    {
        public double ScaleX, ScaleY, X, Y, Opacity;
        public double ScaleXVelocity, ScaleYVelocity, XVelocity, YVelocity, OpacityVelocity;
    }

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_animation_step_menu_surface")]
    private static partial int Step(ref State state, double seconds, byte reduced, out byte settled);

    private static readonly ConditionalWeakTable<FrameworkElement, MenuSurfaceSpring> Motions = new();
    private readonly FrameworkElement _surface;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _slide = new();
    private readonly TransformGroup _transform;
    private State _state;
    private bool _running;
    private long _timestamp;
    private TimeSpan _renderingTime;

    private MenuSurfaceSpring(FrameworkElement surface)
    {
        _surface = surface;
        _transform = new TransformGroup { Children = { _scale, _slide } };
        surface.Unloaded += (_, _) => ResetSurface();
        surface.IsVisibleChanged += (_, _) => { if (!surface.IsVisible) ResetSurface(); };
    }

    internal static void Open(FrameworkElement surface, Point origin, double scale, double x, double y, bool fresh)
    {
        var motion = Motions.GetValue(surface, static element => new MenuSurfaceSpring(element));
        // A repeated open request has the same destination. Keep every channel's
        // current velocity and origin instead of rebuilding its animation clocks.
        if (motion._running && !fresh) return;
        Matrix rendered = surface.RenderTransform.Value;
        motion._state = new State
        {
            ScaleX = fresh ? scale : rendered.M11,
            ScaleY = fresh ? 1 - (1 - scale) * .65 : rendered.M22,
            X = fresh ? x : rendered.OffsetX,
            Y = fresh ? y : rendered.OffsetY,
            Opacity = fresh ? 0 : surface.Opacity
        };
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        surface.RenderTransformOrigin = origin;
        surface.RenderTransform = motion._transform;
        motion.Apply();
        if (!motion._running)
        {
            motion._running = true;
            CompositionTarget.Rendering += motion.Render;
        }
        motion._timestamp = Stopwatch.GetTimestamp();
        motion._renderingTime = TimeSpan.MinValue;
        motion.Advance(0);
    }

    internal static void Reset(FrameworkElement surface)
    {
        if (Motions.TryGetValue(surface, out var motion)) motion.ResetSurface();
        else
        {
            surface.BeginAnimation(UIElement.OpacityProperty, null);
            surface.Opacity = 1;
            surface.RenderTransform = Transform.Identity;
        }
    }

    private void Render(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _renderingTime) return;
            _renderingTime = frame.RenderingTime;
        }
        if (!_surface.IsVisible || AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            ResetSurface();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        double seconds = Stopwatch.GetElapsedTime(_timestamp, now).TotalSeconds;
        if (seconds < .9 / AnimationConfig.TargetFps) return;
        _timestamp = now;
        Advance(seconds);
    }

    private void Advance(double seconds)
    {
        try
        {
            if (Step(ref _state, seconds, 0, out byte settled) != 0) { ResetSurface(); return; }
            Apply();
            if (settled != 0) Stop();
        }
        catch (DllNotFoundException) { ResetSurface(); }
        catch (EntryPointNotFoundException) { ResetSurface(); }
    }

    private void Apply()
    {
        _scale.ScaleX = _state.ScaleX;
        _scale.ScaleY = _state.ScaleY;
        _slide.X = _state.X;
        _slide.Y = _state.Y;
        _surface.Opacity = Math.Clamp(_state.Opacity, 0, 1);
    }

    private void Stop()
    {
        CompositionTarget.Rendering -= Render;
        _running = false;
    }

    private void ResetSurface()
    {
        Stop();
        _state = new State { ScaleX = 1, ScaleY = 1, Opacity = 1 };
        _surface.BeginAnimation(UIElement.OpacityProperty, null);
        Apply();
        _surface.RenderTransform = Transform.Identity;
    }
}
