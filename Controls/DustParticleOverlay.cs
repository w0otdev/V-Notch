using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>
/// A high-performance particle overlay that renders shimmering floating dust motes ("bụi bay mờ")
/// over masked passwords. When clicked, particles burst and dissolve outward ("tan ra"),
/// clearing the blur to reveal the password underneath.
/// </summary>
public sealed class DustParticleOverlay : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(DustParticleOverlay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsActiveChanged));

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

    private struct DustParticle
    {
        public double X;
        public double Y;
        public double BaseX;
        public double BaseY;
        public double Vx;
        public double Vy;
        public double Radius;
        public double BaseRadius;
        public double Alpha;
        public double TargetAlpha;
        public double Phase;
        public double Speed;
        public int BrushIndex;
    }

    private static readonly Brush BackdropBrush = CreateFrozenBrush(Color.FromArgb(0x35, 0x0E, 0x0E, 0x14));
    private static readonly Brush CapsuleBg = CreateFrozenBrush(Color.FromArgb(0x75, 0x12, 0x12, 0x1C));
    private static readonly Pen CapsuleBorder = CreateFrozenPen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 1.0);
    private static readonly Brush TextPillBrush = CreateFrozenBrush(Color.FromArgb(0xD5, 0xF5, 0xF5, 0xFA));
    private static readonly Brush[] DustBrushes =
    [
        CreateFrozenBrush(Color.FromArgb(0xEE, 0xF8, 0xFA, 0xFF)),
        CreateFrozenBrush(Color.FromArgb(0xD8, 0xA8, 0xD4, 0xFF)),
        CreateFrozenBrush(Color.FromArgb(0xC0, 0xD0, 0xE4, 0xF8)),
        CreateFrozenBrush(Color.FromArgb(0xF0, 0xFF, 0xF8, 0xD8))
    ];

    private static Brush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var p = new Pen(CreateFrozenBrush(color), thickness);
        p.Freeze();
        return p;
    }

    private readonly DustParticle[] _particles = new DustParticle[38];
    private bool _particlesInitialized;
    private bool _isDissolving;
    private long _dissolveStartMs;
    private long _lastFrameMs;
    private double _dissolveProgress;
    private bool _isRenderingAttached;

    public DustParticleOverlay()
    {
        Cursor = Cursors.Hand;
        Loaded += (_, _) => UpdateRenderingState();
        Unloaded += (_, _) => StopRendering();
        IsVisibleChanged += (_, _) => UpdateRenderingState();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DustParticleOverlay overlay)
        {
            bool active = (bool)e.NewValue;
            overlay.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            overlay.IsHitTestVisible = active && !overlay.IsDissolved;
            overlay.UpdateRenderingState();
        }
    }

    private static void OnIsDissolvedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DustParticleOverlay overlay)
        {
            bool dissolved = (bool)e.NewValue;
            overlay.IsHitTestVisible = overlay.IsActive && !dissolved;
            if (dissolved && !overlay._isDissolving)
            {
                overlay.StopRendering();
                overlay.InvalidateVisual();
            }
            else if (!dissolved)
            {
                overlay.Reform();
            }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.NewSize.Width > 5 && sizeInfo.NewSize.Height > 5)
        {
            InitParticles();
        }
    }

    private void InitParticles()
    {
        double w = Math.Max(20.0, ActualWidth);
        double h = Math.Max(20.0, ActualHeight);
        var rand = new Random(GetHashCode() ^ Environment.TickCount);

        for (int i = 0; i < _particles.Length; i++)
        {
            double bx = 4.0 + rand.NextDouble() * (w - 8.0);
            double by = 4.0 + rand.NextDouble() * (h - 8.0);
            double r = 1.2 + rand.NextDouble() * 1.8;
            double alpha = 0.35 + rand.NextDouble() * 0.50;

            _particles[i] = new DustParticle
            {
                BaseX = bx,
                BaseY = by,
                X = bx,
                Y = by,
                BaseRadius = r,
                Radius = r,
                TargetAlpha = alpha,
                Alpha = alpha,
                Phase = rand.NextDouble() * Math.PI * 2.0,
                Speed = 0.8 + rand.NextDouble() * 1.0,
                BrushIndex = rand.Next(DustBrushes.Length)
            };
        }
        _particlesInitialized = true;
    }

    private void UpdateRenderingState()
    {
        if (IsActive && IsLoaded && IsVisible && (!IsDissolved || _isDissolving))
            StartRendering();
        else
            StopRendering();
    }

    private void StartRendering()
    {
        if (_isRenderingAttached) return;
        _lastFrameMs = Environment.TickCount64;
        CompositionTarget.Rendering += OnRendering;
        _isRenderingAttached = true;
    }

    private void StopRendering()
    {
        if (!_isRenderingAttached) return;
        CompositionTarget.Rendering -= OnRendering;
        _isRenderingAttached = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsActive || !IsLoaded || !IsVisible)
        {
            StopRendering();
            return;
        }

        long now = Environment.TickCount64;
        double dt = Math.Clamp((now - _lastFrameMs) * 0.001, 0.001, 0.050);
        _lastFrameMs = now;

        if (_isDissolving)
        {
            double elapsed = (now - _dissolveStartMs) / 380.0;
            _dissolveProgress = Math.Clamp(elapsed, 0.0, 1.0);
            double fade = Math.Max(0.0, 1.0 - _dissolveProgress);

            for (int i = 0; i < _particles.Length; i++)
            {
                ref var p = ref _particles[i];
                p.X += p.Vx * dt;
                p.Y += p.Vy * dt;
                p.Vx *= 0.93;
                p.Vy *= 0.93;
                p.Radius = p.BaseRadius * (1.0 + _dissolveProgress * 1.8);
                p.Alpha = p.TargetAlpha * fade * fade;
            }

            InvalidateVisual();

            if (_dissolveProgress >= 1.0)
            {
                _isDissolving = false;
                IsDissolved = true;
                IsHitTestVisible = false;
                StopRendering();
                InvalidateVisual();
            }
        }
        else if (!IsDissolved)
        {
            double time = now * 0.001;
            for (int i = 0; i < _particles.Length; i++)
            {
                ref var p = ref _particles[i];
                p.X = p.BaseX + Math.Sin(time * p.Speed + p.Phase) * 5.0;
                p.Y = p.BaseY + Math.Cos(time * 0.8 * p.Speed + p.Phase * 1.3) * 3.5;
                p.Alpha = p.TargetAlpha * (0.75 + 0.25 * Math.Sin(time * 2.2 + p.Phase));
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
        if (_isDissolving || IsDissolved) return;
        _isDissolving = true;
        _dissolveStartMs = Environment.TickCount64;
        _lastFrameMs = _dissolveStartMs;
        Point origin = clickPoint ?? new Point(ActualWidth / 2, ActualHeight / 2);

        var rand = Random.Shared;
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            double dx = p.X - origin.X;
            double dy = p.Y - origin.Y;
            double dist = Math.Max(2.0, Math.Sqrt(dx * dx + dy * dy));
            double angle = Math.Atan2(dy, dx) + (rand.NextDouble() - 0.5) * 0.7;
            double speed = 120.0 + rand.NextDouble() * 240.0;
            p.Vx = Math.Cos(angle) * speed;
            p.Vy = Math.Sin(angle) * speed;
        }

        AnimateBlur(fromRadius: 12.0, toRadius: 0.0);
        StartRendering();
    }

    public void Reform()
    {
        _isDissolving = false;
        _dissolveProgress = 0.0;
        InitParticles();
        AnimateBlur(fromRadius: 0.0, toRadius: 12.0);
        IsHitTestVisible = true;
        StartRendering();
        InvalidateVisual();
    }

    private void AnimateBlur(double fromRadius, double toRadius)
    {
        if (Parent is Grid grid)
        {
            var textBlock = grid.Children.OfType<TextBlock>().FirstOrDefault();
            if (textBlock != null)
            {
                if (textBlock.Effect is not BlurEffect blur)
                {
                    textBlock.Effect = blur = new BlurEffect { RenderingBias = RenderingBias.Performance };
                }
                blur.BeginAnimation(BlurEffect.RadiusProperty, null);
                blur.Radius = fromRadius;
                var anim = new DoubleAnimation(fromRadius, toRadius, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = TrayMotion.EaseOut
                };
                Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
                blur.BeginAnimation(BlurEffect.RadiusProperty, anim);
            }
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsActive && !IsDissolved)
        {
            e.Handled = true;
            Point clickPoint = e.GetPosition(this);
            Dissolve(clickPoint);
            RaiseEvent(new RoutedEventArgs(DissolveRequestedEvent, this));
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!IsActive || (IsDissolved && !_isDissolving) || ActualWidth <= 0 || ActualHeight <= 0) return;

        if (!_particlesInitialized)
        {
            InitParticles();
        }

        // 1. Frosted Backdrop Scrim over text
        if (!_isDissolving)
        {
            dc.DrawRoundedRectangle(BackdropBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        }
        else
        {
            double fade = Math.Max(0.0, 1.0 - _dissolveProgress);
            if (fade > 0.01)
            {
                dc.PushOpacity(fade);
                dc.DrawRoundedRectangle(BackdropBrush, null, new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
                dc.Pop();
            }
        }

        // 2. Floating / Dispersing Dust Particles
        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            if (p.Alpha <= 0.01) continue;

            dc.PushOpacity(p.Alpha);
            var brush = DustBrushes[p.BrushIndex];
            dc.DrawEllipse(brush, null, new Point(p.X, p.Y), p.Radius, p.Radius);
            dc.Pop();
        }

        // 3. Central Hint Capsule (Visible when idle)
        if (!_isDissolving && ActualWidth > 55 && ActualHeight > 22)
        {
            double pillW = Math.Min(88, ActualWidth - 16);
            double pillH = 22;
            double pillX = (ActualWidth - pillW) / 2;
            double pillY = (ActualHeight - pillH) / 2;
            Rect pillRect = new(pillX, pillY, pillW, pillH);

            dc.DrawRoundedRectangle(CapsuleBg, CapsuleBorder, pillRect, pillH / 2, pillH / 2);

            var text = new FormattedText(
                "••••••••",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("pack://application:,,,/V-Notch;component/Fonts/#SF Pro Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                11.5,
                TextPillBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(text, new Point(pillX + (pillW - text.Width) / 2, pillY + (pillH - text.Height) / 2));
        }
    }
}
