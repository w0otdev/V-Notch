using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// Telegram-style high-density spoiler dust overlay ("bụi bay mờ").
/// Uses a zero-allocation struct particle engine, R2 quasirandom stratified spawning,
/// pre-frozen alpha brush LUTs (zero PushOpacity/Pop overhead), and a shared static
/// frame coordinator with adaptive frame pacing.
/// </summary>
public sealed partial class DustParticleOverlay : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(DustParticleOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnIsActiveChanged));

    public static readonly DependencyProperty IsDissolvedProperty = DependencyProperty.Register(
        nameof(IsDissolved), typeof(bool), typeof(DustParticleOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsDissolvedChanged));

    public static readonly RoutedEvent DissolveRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(DissolveRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DustParticleOverlay));

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool IsDissolved { get => (bool)GetValue(IsDissolvedProperty); set => SetValue(IsDissolvedProperty, value); }

    public event RoutedEventHandler DissolveRequested
    {
        add => AddHandler(DissolveRequestedEvent, value);
        remove => RemoveHandler(DissolveRequestedEvent, value);
    }

    private const int MinParticles = 24;
    private const int MaxParticles = 180;
    private const int AlphaLevels = 20;
    private const int TintVariants = 3;
    private const double MaskedBlurRadius = 12.0;
    private const double DissolveDurationMs = 360.0;
    private const double ReformDurationMs = 240.0;
    private const int IdleTargetFps = 45;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DustParticle
    {
        public float X;
        public float Y;
        public float Vx;
        public float Vy;
        public float BaseRadius;
        public float Radius;
        public float PeakAlpha;
        public float Alpha;
        public float Life;      // 0..1 normalized lifecycle progress
        public float LifeSpeed; // 1 / lifetimeSeconds
        public float TwinklePhase;
        public byte TintIndex;
    }

    [LibraryImport("VNotch.Native")]
    private static partial int vn_dust_particles_init(
        Span<DustParticle> particles,
        int capacity,
        double width,
        double height,
        ref uint ioRngState,
        ref int ioR2Sequence,
        out int outActiveCount);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_dust_particles_step(
        Span<DustParticle> particles,
        int activeCount,
        double width,
        double height,
        float dt,
        byte isDissolving,
        double dissolveProgress,
        double reformProgress,
        ref uint ioRngState,
        ref int ioR2Sequence);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_dust_particles_dissolve_impulse(
        Span<DustParticle> particles,
        int activeCount,
        double width,
        double height,
        float originX,
        float originY,
        ref uint ioRngState);

    // Pre-frozen 2D LUT [tintIndex * AlphaLevels + alphaBucket] to eliminate dc.PushOpacity/Pop per particle.
    private static readonly Brush[] BrushLut = CreateBrushLut();

    private static Brush[] CreateBrushLut()
    {
        // Telegram monochrome cool-silver & crisp white spoiler palette
        ReadOnlySpan<(byte R, byte G, byte B)> tints =
        [
            (0xF6, 0xF8, 0xFC), // Crisp ice white
            (0xE0, 0xE6, 0xF2), // Soft silver-blue mist
            (0xC8, 0xD1, 0xE0)  // Cool slate-silver grain
        ];

        var lut = new Brush[TintVariants * AlphaLevels];
        for (int t = 0; t < TintVariants; t++)
        {
            var (r, g, b) = tints[t];
            for (int a = 0; a < AlphaLevels; a++)
            {
                double normalized = (a + 1) / (double)AlphaLevels;
                byte alphaByte = (byte)Math.Clamp((int)Math.Round(normalized * 242.0), 8, 245);
                var brush = new SolidColorBrush(Color.FromArgb(alphaByte, r, g, b));
                brush.Freeze();
                lut[t * AlphaLevels + a] = brush;
            }
        }
        return lut;
    }

    private DustParticle[] _particles = [];
    private int _activeCount;
    private uint _rngState;
    private int _r2SequenceIndex;
    private bool _particlesInitialized;
    private bool _isDissolving;
    private bool _isReforming;
    private long _transitionStartMs;
    private long _lastStepMs;
    private AnimationFrameClock _simulationFrameClock;

    private double _reformProgress = 1.0;

    public DustParticleOverlay()
    {
        Cursor = Cursors.Hand;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        _rngState = (uint)(RuntimeHelpers.GetHashCode(this) ^ Environment.TickCount) | 1u;

        Loaded += OnLifecycleStateChanged;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
    }

    // Return zero desired size so the sibling TextBlock strictly dictates the container's
    // width and height when HorizontalAlignment="Left", preventing the dust cloud from
    // exceeding the exact rendered length of the secret.
    protected override Size MeasureOverride(Size availableSize)
    {
        return default;
    }

    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (IsActive)
        {
            ResetSiblingBlur(IsDissolved ? 0.0 : MaskedBlurRadius);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _isDissolving = false;
        _isReforming = false;

        _reformProgress = 1.0;
        _particlesInitialized = false;
        if (IsActive)
        {
            if (ActualWidth > 4 && ActualHeight > 4)
            {
                InitParticles(ActualWidth, ActualHeight);
            }
            ResetSiblingBlur(IsDissolved ? 0.0 : MaskedBlurRadius);
        }
        else
        {
            ResetSiblingBlur(0.0);
        }
        IsHitTestVisible = IsActive && !IsDissolved;
        UpdateRenderingState();
        InvalidateVisual();
    }

    private void OnLifecycleStateChanged(object sender, RoutedEventArgs e)
    {
        if (IsActive && !_isDissolving)
        {
            ResetSiblingBlur(IsDissolved ? 0.0 : MaskedBlurRadius);
        }
        UpdateRenderingState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateRenderingState();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DustParticleOverlay overlay) return;

        bool active = (bool)e.NewValue;
        overlay._isReforming = false;
        if (!active)
        {
            overlay._isDissolving = false;
            overlay.ResetSiblingBlur(0.0);
        }
        else
        {
            overlay.ResetSiblingBlur(overlay.IsDissolved ? 0.0 : MaskedBlurRadius);
        }

        overlay.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        overlay.IsHitTestVisible = active && !overlay.IsDissolved;
        overlay.UpdateRenderingState();
    }

    private static void OnIsDissolvedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DustParticleOverlay overlay) return;

        bool dissolved = (bool)e.NewValue;
        overlay.IsHitTestVisible = overlay.IsActive && !dissolved;
        if (!overlay.IsActive)
        {
            overlay._isDissolving = false;
            overlay._isReforming = false;
            overlay.StopRendering();
            overlay.ResetSiblingBlur(0.0);
            return;
        }

        if (dissolved && !overlay._isDissolving)
        {
            overlay.Dissolve();
        }
        else if (!dissolved)
        {
            overlay.Reform();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_isDissolving && sizeInfo.NewSize.Width > 4 && sizeInfo.NewSize.Height > 4)
        {
            InitParticles(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
        }
    }

    private void InitParticles(double width, double height)
    {
        if (_particles.Length == 0)
            _particles = new DustParticle[MaxParticles];

        vn_dust_particles_init(
            _particles,
            _particles.Length,
            width,
            height,
            ref _rngState,
            ref _r2SequenceIndex,
            out _activeCount);

        _particlesInitialized = true;
    }

    private void UpdateRenderingState()
    {
        bool shouldAnimate = IsActive && IsLoaded && IsVisible &&
                             (!IsDissolved || _isDissolving) &&
                             (!AnimationConfig.ReduceMotion || _isDissolving || _isReforming);

        if (shouldAnimate)
            StartRendering();
        else
            StopRendering();
    }

    private void StartRendering()
    {
        ResetFramePacing(Environment.TickCount64);
        FrameCoordinator.Register(this);
    }

    internal void ResetFramePacing(long nowMs)
    {
        _lastStepMs = nowMs;
        _simulationFrameClock.Reset(ToFrameTicks(nowMs));
    }

    private static long ToFrameTicks(long milliseconds) =>
        (long)Math.Round(milliseconds * (System.Diagnostics.Stopwatch.Frequency / 1000.0));

    internal bool TryAdvanceSimulation(long nowMs, bool isTransitioning, out float dt, TimeSpan? renderingTime = null)
    {
        int targetFps = isTransitioning ? AnimationConfig.TargetFps : Math.Min(IdleTargetFps, AnimationConfig.TargetFps);
        dt = 0;
        bool advance = renderingTime.HasValue
            ? _simulationFrameClock.TryAdvance(renderingTime.Value, targetFps, out _)
            : _simulationFrameClock.TryAdvance(ToFrameTicks(nowMs), targetFps, out _);
        if (!advance) return false;
        dt = (float)Math.Clamp((nowMs - _lastStepMs) * 0.001, 0.001, 0.050);
        _lastStepMs = nowMs;
        return true;
    }

    private void StopRendering()
    {
        FrameCoordinator.Unregister(this);
    }

    internal void OnCoordinatorTick(long nowMs, TimeSpan? renderingTime = null)
    {
        if (!IsActive || !IsLoaded || !IsVisible)
        {
            StopRendering();
            return;
        }

        bool isTransitioning = _isDissolving || _isReforming;
        if (!TryAdvanceSimulation(nowMs, isTransitioning, out float dt, renderingTime)) return;

        double w = Math.Max(12.0, ActualWidth);
        double h = Math.Max(14.0, ActualHeight);
        if (!_particlesInitialized)
        {
            InitParticles(w, h);
        }

        Span<DustParticle> span = _particles.AsSpan(0, _activeCount);

        if (_isDissolving)
        {
            double progress = Math.Clamp((nowMs - _transitionStartMs) / DissolveDurationMs, 0.0, 1.0);
            vn_dust_particles_step(
                span,
                span.Length,
                w,
                h,
                dt,
                isDissolving: 1,
                dissolveProgress: progress,
                reformProgress: _reformProgress,
                ref _rngState,
                ref _r2SequenceIndex);

            InvalidateVisual();

            if (progress >= 1.0)
            {
                _isDissolving = false;
                SetCurrentValue(IsDissolvedProperty, true);
                IsHitTestVisible = false;
                StopRendering();
                InvalidateVisual();
            }
            return;
        }

        if (_isReforming)
        {
            double progress = Math.Clamp((nowMs - _transitionStartMs) / ReformDurationMs, 0.0, 1.0);
            _reformProgress = 1.0 - Math.Pow(1.0 - progress, 3.0);
            if (progress >= 1.0)
            {
                _isReforming = false;
                _reformProgress = 1.0;
                if (AnimationConfig.ReduceMotion)
                {
                    StopRendering();
                }
            }
        }

        if (!IsDissolved)
        {
            vn_dust_particles_step(
                span,
                span.Length,
                w,
                h,
                dt,
                isDissolving: 0,
                dissolveProgress: 0.0,
                reformProgress: _reformProgress,
                ref _rngState,
                ref _r2SequenceIndex);

            InvalidateVisual();
        }
        else
        {
            StopRendering();
        }
    }

    public void Dissolve(Point? clickPoint = null)
    {
        if (_isDissolving) return;
        if (!IsLoaded || AnimationConfig.ReduceMotion)
        {
            _isDissolving = false;
            _isReforming = false;
            StopRendering();
            ResetSiblingBlur(0.0);
            InvalidateVisual();
            return;
        }
        _isDissolving = true;
        _isReforming = false;
        _transitionStartMs = Environment.TickCount64;
        _lastStepMs = _transitionStartMs;

        double w = Math.Max(12.0, ActualWidth);
        double h = Math.Max(14.0, ActualHeight);
        if (!_particlesInitialized)
        {
            InitParticles(w, h);
        }

        float originX = (float)(clickPoint?.X ?? (w * 0.5));
        float originY = (float)(clickPoint?.Y ?? (h * 0.5));
        Span<DustParticle> span = _particles.AsSpan(0, _activeCount);
        vn_dust_particles_dissolve_impulse(span, span.Length, w, h, originX, originY, ref _rngState);

        AnimateBlur(fromRadius: MaskedBlurRadius, toRadius: 0.0);
        StartRendering();
    }

    public void Reform()
    {
        _isDissolving = false;

        _isReforming = true;
        _reformProgress = 0.0;
        _transitionStartMs = Environment.TickCount64;
        _lastStepMs = _transitionStartMs;

        InitParticles(ActualWidth, ActualHeight);
        if (!IsActive)
        {
            _isReforming = false;
            _reformProgress = 1.0;
            IsHitTestVisible = false;
            ResetSiblingBlur(0.0);
            UpdateRenderingState();
            InvalidateVisual();
            return;
        }

        if (!IsLoaded || AnimationConfig.ReduceMotion)
        {
            _isReforming = false;
            _reformProgress = 1.0;
            ResetSiblingBlur(MaskedBlurRadius);
        }
        else
        {
            AnimateBlur(fromRadius: 0.0, toRadius: MaskedBlurRadius);
        }

        IsHitTestVisible = !IsDissolved;
        UpdateRenderingState();
        InvalidateVisual();
    }

    private TextBlock? FindSiblingTextBlock()
    {
        if (Parent is not Grid grid) return null;
        for (int i = 0; i < grid.Children.Count; i++)
        {
            if (grid.Children[i] is TextBlock tb)
            {
                return tb;
            }
        }
        return null;
    }

    private void ResetSiblingBlur(double targetRadius)
    {
        var textBlock = FindSiblingTextBlock();
        if (textBlock == null) return;

        if (textBlock.Effect is BlurEffect blur)
        {
            blur.BeginAnimation(BlurEffect.RadiusProperty, null);
            blur.SetCurrentValue(BlurEffect.RadiusProperty, targetRadius);
        }

        // Hide the underlying bullet mask while covered by spoiler dust so it doesn't
        // cast a bright white blurred blob on the left side under the particles.
        double targetOpacity = targetRadius > 0.01 ? 0.0 : 1.0;
        textBlock.BeginAnimation(OpacityProperty, null);
        textBlock.SetCurrentValue(OpacityProperty, targetOpacity);
    }

    private void AnimateBlur(double fromRadius, double toRadius)
    {
        var textBlock = FindSiblingTextBlock();
        if (textBlock == null) return;

        if (textBlock.Effect is not BlurEffect blur)
        {
            textBlock.Effect = blur = new BlurEffect { RenderingBias = RenderingBias.Performance };
        }

        blur.BeginAnimation(BlurEffect.RadiusProperty, null);
        blur.SetCurrentValue(BlurEffect.RadiusProperty, toRadius);

        double fromOpacity = toRadius > 0.01 ? 1.0 : 0.0;
        double toOpacity = toRadius > 0.01 ? 0.0 : 1.0;
        textBlock.BeginAnimation(OpacityProperty, null);
        textBlock.SetCurrentValue(OpacityProperty, toOpacity);

        if (!IsLoaded || AnimationConfig.ReduceMotion) return;

        var duration = TimeSpan.FromMilliseconds(300);
        var blurAnim = new DoubleAnimation(fromRadius, toRadius, duration)
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        var opacityAnim = new DoubleAnimation(fromOpacity, toOpacity, duration)
        {
            EasingFunction = TrayMotion.EaseOut,
            FillBehavior = FillBehavior.Stop
        };

        Timeline.SetDesiredFrameRate(blurAnim, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(opacityAnim, AnimationConfig.TargetFps);
        blur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);
        textBlock.BeginAnimation(OpacityProperty, opacityAnim);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsActive && !IsDissolved)
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(DissolveRequestedEvent, this));
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!IsActive || (IsDissolved && !_isDissolving) || ActualWidth <= 0 || ActualHeight <= 0) return;

        if (!_particlesInitialized)
        {
            InitParticles(ActualWidth, ActualHeight);
        }

        // Transparent hit-test surface across the entire spoiler area so clicking between
        // fine dust motes still triggers Dissolve reliably.
        if (!_isDissolving)
        {
            drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        // Render particles using pre-frozen Alpha/Tint LUT brushes (zero PushOpacity/Pop calls, zero heap allocations).
        ReadOnlySpan<DustParticle> span = _particles.AsSpan(0, _activeCount);
        for (int i = 0; i < span.Length; i++)
        {
            ref readonly var p = ref span[i];
            if (p.Alpha <= 0.015f) continue;

            int alphaBucket = (int)(p.Alpha * (AlphaLevels - 1));
            if (alphaBucket < 0) continue;
            if (alphaBucket >= AlphaLevels) alphaBucket = AlphaLevels - 1;

            Brush brush = BrushLut[p.TintIndex * AlphaLevels + alphaBucket];
            drawingContext.DrawEllipse(brush, null, new Point(p.X, p.Y), p.Radius, p.Radius);
        }
    }

    /// <summary>
    /// Shared static render coordinator so multiple visible spoiler overlays share a single
    /// CompositionTarget.Rendering subscription.
    /// </summary>
    private static class FrameCoordinator
    {
        private static readonly List<DustParticleOverlay> ActiveOverlays = new(4);
        private static bool _isSubscribed;

        public static void Register(DustParticleOverlay overlay)
        {
            if (!ActiveOverlays.Contains(overlay))
            {
                ActiveOverlays.Add(overlay);
            }

            if (!_isSubscribed && ActiveOverlays.Count > 0)
            {
                CompositionTarget.Rendering += OnCompositionTargetRendering;
                _isSubscribed = true;
            }
        }

        public static void Unregister(DustParticleOverlay overlay)
        {
            ActiveOverlays.Remove(overlay);
            if (_isSubscribed && ActiveOverlays.Count == 0)
            {
                CompositionTarget.Rendering -= OnCompositionTargetRendering;
                _isSubscribed = false;
            }
        }

        private static void OnCompositionTargetRendering(object? sender, EventArgs e)
        {
            if (ActiveOverlays.Count == 0)
            {
                CompositionTarget.Rendering -= OnCompositionTargetRendering;
                _isSubscribed = false;
                return;
            }

            long now = Environment.TickCount64;
            TimeSpan? renderingTime = (e as RenderingEventArgs)?.RenderingTime;
            for (int i = ActiveOverlays.Count - 1; i >= 0; i--)
            {
                if (i < ActiveOverlays.Count)
                {
                    ActiveOverlays[i].OnCoordinatorTick(now, renderingTime);
                }
            }
        }
    }
}
