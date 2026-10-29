using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
public sealed class DustParticleOverlay : FrameworkElement
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
    private const long IdleFrameIntervalMs = 22; // ~45 FPS for sub-pixel idle drift (zero wasted GPU work on 120/240Hz displays)

    // Martin Roberts' R2 low-discrepancy quasirandom constants (plastic constant phi_2 = 1.324717957244746)
    private const double R2AlphaX = 0.7548776662466927;
    private const double R2AlphaY = 0.5698402909980532;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float NextFloat()
    {
        // Xorshift32: branchless, zero-allocation PRNG
        uint x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x;
        return (x & 0x00FFFFFF) * (1.0f / 16777216.0f);
    }

    private void InitParticles(double width, double height)
    {
        double w = Math.Max(12.0, width);
        double h = Math.Max(14.0, height);

        // Scale particle count with area (~1 particle per 22 sq px) to maintain uniform
        // Telegram micro-grain density across both short PINs and long multi-line secrets
        int targetCount = (int)Math.Round((w * h) / 22.0);
        _activeCount = Math.Clamp(targetCount, MinParticles, MaxParticles);
        if (_particles.Length == 0)
            _particles = new DustParticle[MaxParticles];

        Span<DustParticle> span = _particles.AsSpan(0, _activeCount);
        for (int i = 0; i < span.Length; i++)
        {
            SpawnParticle(ref span[i], w, h, randomLifePhase: true);
        }

        _particlesInitialized = true;
    }

    private void SpawnParticle(ref DustParticle p, double width, double height, bool randomLifePhase)
    {
        int seq = ++_r2SequenceIndex;
        double u = (0.5 + seq * R2AlphaX) % 1.0;
        double v = (0.5 + seq * R2AlphaY) % 1.0;

        // Add subtle jitter around the low-discrepancy R2 lattice point
        double jitterX = (NextFloat() - 0.5) * 4.5;
        double jitterY = (NextFloat() - 0.5) * 3.0;

        double padX = Math.Min(3.0, width * 0.04);
        double padY = Math.Min(2.0, height * 0.08);
        double usableW = Math.Max(4.0, width - padX * 2.0);
        double usableH = Math.Max(4.0, height - padY * 2.0);

        float x = (float)Math.Clamp(padX + u * usableW + jitterX, padX, width - padX);
        float y = (float)Math.Clamp(padY + v * usableH + jitterY, padY, height - padY);

        // 3-tier Telegram size distribution: 62% micro specks, 28% medium motes, 10% crisp highlights
        float sizeRoll = NextFloat();
        float radius;
        if (sizeRoll < 0.62f)
            radius = 0.62f + NextFloat() * 0.24f;
        else if (sizeRoll < 0.90f)
            radius = 0.88f + NextFloat() * 0.24f;
        else
            radius = 1.14f + NextFloat() * 0.22f;





        // Isotropic drift velocity (Telegram spoiler particles drift continuously across bounds)
        float angle = NextFloat() * MathF.Tau;
        float speed = 7.5f + NextFloat() * 14.5f;

        float lifetime = 0.60f + NextFloat() * 0.85f;
        float life = randomLifePhase ? NextFloat() : 0.0f;
        float peakAlpha = sizeRoll < 0.62f
            ? 0.42f + NextFloat() * 0.46f
            : 0.55f + NextFloat() * 0.40f;

        byte tint = (byte)(seq % TintVariants);

        p.X = x;
        p.Y = y;
        p.Vx = MathF.Cos(angle) * speed;
        p.Vy = MathF.Sin(angle) * speed * 0.78f;
        p.BaseRadius = radius;
        p.Radius = radius;
        p.PeakAlpha = peakAlpha;
        p.Life = life;
        p.LifeSpeed = 1.0f / lifetime;
        p.TwinklePhase = NextFloat() * MathF.Tau;
        p.TintIndex = tint;
        p.Alpha = ComputeLifecycleAlpha(life, peakAlpha, p.TwinklePhase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeLifecycleAlpha(float life, float peakAlpha, float twinklePhase)
    {
        // Smooth envelope: quick fade-in (0..0.22), shimmering plateau (0.22..0.74), soft fade-out (0.74..1.0)
        float envelope;
        if (life < 0.22f)
        {
            float t = life * (1.0f / 0.22f);
            envelope = t * t * (3.0f - 2.0f * t);
        }
        else if (life > 0.74f)
        {
            float t = (1.0f - life) * (1.0f / 0.26f);
            envelope = Math.Clamp(t * t * (3.0f - 2.0f * t), 0.0f, 1.0f);
        }
        else
        {
            envelope = 1.0f;
        }

        float twinkle = 0.82f + 0.18f * MathF.Sin(life * MathF.Tau * 1.5f + twinklePhase);
        return peakAlpha * envelope * twinkle;
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
        _lastStepMs = Environment.TickCount64;
        FrameCoordinator.Register(this);
    }

    private void StopRendering()
    {
        FrameCoordinator.Unregister(this);
    }

    internal void OnCoordinatorTick(long nowMs)
    {
        if (!IsActive || !IsLoaded || !IsVisible)
        {
            StopRendering();
            return;
        }

        bool isTransitioning = _isDissolving || _isReforming;
        long elapsedMs = nowMs - _lastStepMs;

        // Pace idle simulation to ~45 FPS to save CPU/GPU on high-refresh-rate monitors
        if (!isTransitioning && elapsedMs < IdleFrameIntervalMs)
        {
            return;
        }

        float dt = (float)Math.Clamp(elapsedMs * 0.001, 0.001, 0.050);
        _lastStepMs = nowMs;

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


            // Ease-out drag & quadratic alpha decay during shockwave burst
            float drag = MathF.Exp(-4.8f * dt);
            float remain = (float)(1.0 - progress);
            float alphaFade = remain * remain;
            float radiusScale = 1.0f - (float)progress * 0.35f;

            for (int i = 0; i < span.Length; i++)
            {
                ref var p = ref span[i];
                p.X += p.Vx * dt;
                p.Y += p.Vy * dt;
                p.Vx *= drag;
                p.Vy *= drag;
                p.Radius = Math.Max(0.35f, p.BaseRadius * radiusScale);
                p.Alpha = p.PeakAlpha * alphaFade;
            }

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
            float maxW = (float)w - 1.5f;
            float maxH = (float)h - 1.5f;
            float reformFactor = (float)_reformProgress;

            for (int i = 0; i < span.Length; i++)
            {
                ref var p = ref span[i];
                p.Life += p.LifeSpeed * dt;

                // Subtle curl turbulence keeps trajectories organic
                float curl = MathF.Sin(p.TwinklePhase + p.Life * 4.2f) * 6.0f;
                p.X += (p.Vx + curl * 0.25f) * dt;
                p.Y += (p.Vy - curl * 0.18f) * dt;

                if (p.Life >= 1.0f || p.X < 1.5f || p.X > maxW || p.Y < 1.5f || p.Y > maxH)
                {
                    SpawnParticle(ref p, w, h, randomLifePhase: false);
                }

                p.Alpha = ComputeLifecycleAlpha(p.Life, p.PeakAlpha, p.TwinklePhase) * reformFactor;
            }

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
        float maxDist = MathF.Max(24.0f, MathF.Sqrt((float)(w * w + h * h)));

        Span<DustParticle> span = _particles.AsSpan(0, _activeCount);
        for (int i = 0; i < span.Length; i++)
        {
            ref var p = ref span[i];
            float dx = p.X - originX;
            float dy = p.Y - originY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            float baseAngle = dist > 0.5f ? MathF.Atan2(dy, dx) : NextFloat() * MathF.Tau;
            float scatterAngle = baseAngle + (NextFloat() - 0.5f) * 0.65f;

            // Radial shockwave: particles near the click receive a stronger initial impulse
            float proximity = 1.0f - Math.Clamp(dist / maxDist, 0.0f, 0.75f);
            float speed = (85.0f + NextFloat() * 165.0f) * (0.75f + 0.55f * proximity);

            p.Vx = MathF.Cos(scatterAngle) * speed;
            p.Vy = MathF.Sin(scatterAngle) * speed;
            p.PeakAlpha = Math.Max(p.Alpha, 0.45f + NextFloat() * 0.45f);
        }

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
            for (int i = ActiveOverlays.Count - 1; i >= 0; i--)
            {
                if (i < ActiveOverlays.Count)
                {
                    ActiveOverlays[i].OnCoordinatorTick(now);
                }
            }
        }
    }
}
