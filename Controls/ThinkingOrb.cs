using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>A compact, transparent particle orb. No clocks run while hidden or idle.</summary>
public sealed partial class ThinkingOrb : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(ThinkingOrb), new FrameworkPropertyMetadata(false, OnActivityChanged));
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ThinkingOrbState), typeof(ThinkingOrb),
        new FrameworkPropertyMetadata(ThinkingOrbState.Working, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is ThinkingOrbState state && Enum.IsDefined(state));
    public static readonly DependencyProperty DotScaleProperty = DependencyProperty.Register(
        nameof(DotScale), typeof(double), typeof(ThinkingOrb),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double scale && double.IsFinite(scale) && scale > 0 && scale <= 3);

    private static readonly SolidColorBrush[] ParticleBrushes = CreateBrushes();
    private readonly ThinkingOrbGeometry.Dot[] _dots = new ThinkingOrbGeometry.Dot[ThinkingOrbGeometry.Capacity];
    private readonly Stopwatch _clock = new();
    private double _lastFrame = double.NegativeInfinity;
    internal bool IsAnimating { get; private set; }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public ThinkingOrbState State { get => (ThinkingOrbState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public double DotScale { get => (double)GetValue(DotScaleProperty); set => SetValue(DotScaleProperty, value); }

    public ThinkingOrb()
    {
        InitializeAgentLayers();
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateClock();
    }

    protected override Size MeasureOverride(Size availableSize) => new(28, 28);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || !double.IsFinite(size)) return;
        if (RandomizeStates)
        {
            RenderAgent(size);
            return;
        }
        int count = ThinkingOrbGeometry.WriteFrame(_dots, State, size, _clock.Elapsed.TotalSeconds);
        double x = (ActualWidth - size) / 2, y = (ActualHeight - size) / 2;
        double dotScale = DotScale;
        for (int i = 0; i < count; i++)
        {
            var dot = _dots[i];
            int gray = (int)Math.Round(Math.Clamp(1 - dot.Ink, 0, 1) * 31);
            int alpha = (int)Math.Round(Math.Clamp(dot.Alpha, 0, 1) * 15);
            drawingContext.DrawEllipse(ParticleBrushes[alpha * 32 + gray], null,
                new Point(dot.X + x, dot.Y + y), dot.Radius * dotScale, dot.Radius * dotScale);
        }
    }

    private static void OnActivityChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var orb = (ThinkingOrb)sender;
        orb.UpdateAgentActivity((bool)args.NewValue);
        if (!(bool)args.NewValue) orb._clock.Reset();
        orb.UpdateClock();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimationConfig.ReduceMotionChanged += OnMotionPreferenceChanged;
        UpdateClock();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AnimationConfig.ReduceMotionChanged -= OnMotionPreferenceChanged;
        StopClock();
    }

    private void OnMotionPreferenceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnMotionPreferenceChanged));
            return;
        }
        UpdateClock();
    }

    private void UpdateClock()
    {
        bool animate = IsActive && IsLoaded && IsVisible && !AnimationConfig.ReduceMotion;
        if (animate && !IsAnimating)
        {
            IsAnimating = true;
            _lastFrame = double.NegativeInfinity;
            _clock.Start();
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!animate) StopClock();
        PublishAgentTransition();
        InvalidateVisual();
    }

    private void StopClock()
    {
        CompositionTarget.Rendering -= OnFrame;
        IsAnimating = false;
        _clock.Stop();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastFrame < 1d / Math.Min(60, AnimationConfig.TargetFps)) return;
        _lastFrame = now;
        AdvanceAgentState(now + _agentTimeOffset);
        PublishAgentTransition();
        InvalidateVisual();
    }

    private static SolidColorBrush[] CreateBrushes()
    {
        var brushes = new SolidColorBrush[512];
        for (int alpha = 0; alpha < 16; alpha++)
        for (int gray = 0; gray < 32; gray++)
        {
            byte channel = (byte)Math.Round(gray * 255d / 31);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 17), channel, channel, channel));
            brush.Freeze();
            brushes[alpha * 32 + gray] = brush;
        }
        return brushes;
    }
}
