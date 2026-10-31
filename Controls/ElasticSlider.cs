using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ElasticSlider : Slider
{
    private const double TrackEdgePad = 10.0;

    [LibraryImport("VNotch.Native", EntryPoint = "vn_animation_compute_elastic_slider_overscroll")]
    private static unsafe partial int NativeComputeElasticSliderOverscroll(
        double overflowPx,
        double usableWidth,
        byte reducedMotion,
        double* outScaleX,
        double* outScaleY);

    #region Dependency Properties

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(ElasticSlider),
            new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(ElasticSlider),
            new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(ElasticSlider),
            new PropertyMetadata(string.Empty, OnUnitChanged));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ElasticSlider s)
        {
            AutomationProperties.SetName(s, s.Label);
            s.UpdateLabelText();
        }
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ElasticSlider s)
        {
            AutomationProperties.SetHelpText(s, s.Description);
            s.UpdateDescriptionText();
        }
    }

    private static void OnUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ElasticSlider s) s.UpdateValueText();
    }

    #endregion

    private static readonly SolidColorBrush MajorTickBrush = CreateFrozenBrush(52);
    private static readonly SolidColorBrush MinorTickBrush = CreateFrozenBrush(24);
    private static readonly SolidColorBrush ActiveMajorTickBrush = CreateFrozenBrush(115);
    private static readonly SolidColorBrush ActiveMinorTickBrush = CreateFrozenBrush(60);
    private static readonly CubicEase SmoothOutEase = CreateFrozenCubicEase();

    private static SolidColorBrush CreateFrozenBrush(byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));
        brush.Freeze();
        return brush;
    }

    private static CubicEase CreateFrozenCubicEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private Border? _rootBorder;
    private Border? _fillBorder;
    private Border? _hoverOverlay;
    private Canvas? _tickCanvas;
    private Rectangle? _indicator;
    private TextBlock? _labelText;
    private TextBlock? _descText;
    private TextBlock? _valueText;
    private ScaleTransform? _rootScale;
    private ScaleTransform? _indicatorScale;
    private bool _isDragging;
    private double _dragOverflow;
    private double _lastAnimatedValue = double.NaN;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_rootBorder != null)
        {
            _rootBorder.SizeChanged -= OnRootBorderSizeChanged;
            _rootBorder.MouseEnter -= OnRootBorderMouseEnter;
            _rootBorder.MouseLeave -= OnRootBorderMouseLeave;
        }

        _rootBorder = Template?.FindName("PART_Root", this) as Border;
        _fillBorder = Template?.FindName("PART_Fill", this) as Border;
        _hoverOverlay = Template?.FindName("PART_HoverOverlay", this) as Border;
        _tickCanvas = Template?.FindName("PART_Ticks", this) as Canvas;
        _indicator = Template?.FindName("PART_Indicator", this) as Rectangle;
        _labelText = Template?.FindName("PART_Label", this) as TextBlock;
        _descText = Template?.FindName("PART_Description", this) as TextBlock;
        _valueText = Template?.FindName("PART_Value", this) as TextBlock;

        if (_rootBorder != null)
        {
            _rootScale = new ScaleTransform(1, 1);
            _rootBorder.RenderTransform = _rootScale;
            if (!_isDragging) _rootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
            _rootBorder.SizeChanged += OnRootBorderSizeChanged;
            _rootBorder.MouseEnter += OnRootBorderMouseEnter;
            _rootBorder.MouseLeave += OnRootBorderMouseLeave;
        }

        if (_indicator != null)
        {
            _indicatorScale = new ScaleTransform(1, 1);
            _indicator.RenderTransform = _indicatorScale;
            _indicator.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        SetFillImmediate();
        UpdateLabelText();
        UpdateDescriptionText();
        UpdateValueText();
        DrawTicks();
        UpdateIndicatorPosition(immediate: true);
    }

    private void OnRootBorderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetFillImmediate();
        DrawTicks();
        UpdateIndicatorPosition(immediate: true);
    }

    private void OnRootBorderMouseEnter(object sender, MouseEventArgs e)
    {
        UpdateInteractiveStateVisuals();
    }

    private void OnRootBorderMouseLeave(object sender, MouseEventArgs e)
    {
        UpdateInteractiveStateVisuals();
    }

    private void UpdateInteractiveStateVisuals()
    {
        bool hovered = _rootBorder?.IsMouseOver == true || IsMouseOver;
        double targetOverlayOpacity = _isDragging ? 1.0 : (hovered ? 0.65 : 0.0);
        AnimateElementOpacity(_hoverOverlay, targetOverlayOpacity, hovered || _isDragging ? 160 : 220);

        double indScaleX = _isDragging ? 1.18 : (hovered ? 1.08 : 1.0);
        double indScaleY = _isDragging ? 1.16 : (hovered ? 1.08 : 1.0);
        AnimateScaleTransform(_indicatorScale, indScaleX, indScaleY, 160);
    }

    private static void AnimateElementOpacity(UIElement? element, double targetOpacity, int durationMs)
    {
        if (element == null) return;
        if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = targetOpacity;
            return;
        }

        var anim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = SmoothOutEase
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        element.BeginAnimation(OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateScaleTransform(ScaleTransform? transform, double targetX, double targetY, int durationMs)
    {
        if (transform == null) return;
        if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleX = targetX;
            transform.ScaleY = targetY;
            return;
        }

        var dur = TimeSpan.FromMilliseconds(durationMs);
        var ax = new DoubleAnimation(targetX, dur) { EasingFunction = SmoothOutEase };
        var ay = new DoubleAnimation(targetY, dur) { EasingFunction = SmoothOutEase };
        Timeline.SetDesiredFrameRate(ax, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(ay, AnimationConfig.TargetFps);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, ax, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, ay, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);

        if (Maximum > Minimum && !_isDragging)
        {
            double snapped = SnapValue(newValue);
            if (Math.Abs(snapped - newValue) > 0.001)
            {
                Value = snapped;
                return;
            }
        }

        UpdateValueText();
        UpdateTickBrushes();
        bool hasPrevious = !double.IsNaN(_lastAnimatedValue) && Math.Abs(oldValue - newValue) > 0.001;
        UpdateIndicatorPosition(immediate: !hasPrevious);

        if (hasPrevious)
            AnimateFillSmooth();
        else
            SetFillImmediate();

        _lastAnimatedValue = newValue;
    }

    protected override void OnMinimumChanged(double oldMinimum, double newMinimum)
    {
        base.OnMinimumChanged(oldMinimum, newMinimum);
        SetFillImmediate();
        DrawTicks();
        UpdateIndicatorPosition(immediate: true);
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        SetFillImmediate();
        DrawTicks();
        UpdateIndicatorPosition(immediate: true);
    }

    #region Tick Marks & Indicator

    private double GetNormalizedFraction()
    {
        if (Maximum <= Minimum) return 0;
        return Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1);
    }

    private void DrawTicks()
    {
        if (_tickCanvas == null || _rootBorder == null) return;
        _tickCanvas.Children.Clear();

        double width = _rootBorder.ActualWidth;
        double height = _rootBorder.ActualHeight;
        if (width <= 0 || height <= 0) return;
        if (Maximum <= Minimum) return;

        int totalTicks = GetTickCount();
        double usableWidth = width - TrackEdgePad * 2.0;
        if (usableWidth <= 0) return;

        double currentFraction = GetNormalizedFraction();

        for (int i = 0; i <= totalTicks; i++)
        {
            double fraction = (double)i / totalTicks;
            double x = TrackEdgePad + fraction * usableWidth;

            bool isMajor = (i % 5 == 0);
            bool isActive = fraction <= currentFraction + 0.001;

            double tickHeight = isMajor ? 14 : 8;
            double tickWidth = isMajor ? 1.8 : 1.1;

            var tick = new Rectangle
            {
                Width = tickWidth,
                Height = tickHeight,
                RadiusX = tickWidth / 2,
                RadiusY = tickWidth / 2,
                Fill = isMajor
                    ? (isActive ? ActiveMajorTickBrush : MajorTickBrush)
                    : (isActive ? ActiveMinorTickBrush : MinorTickBrush),
                Tag = fraction,
                SnapsToDevicePixels = true
            };

            Canvas.SetLeft(tick, x - tickWidth / 2);
            Canvas.SetTop(tick, (height - tickHeight) / 2);
            _tickCanvas.Children.Add(tick);
        }
    }

    private void UpdateTickBrushes()
    {
        if (_tickCanvas == null || _tickCanvas.Children.Count == 0) return;
        double currentFraction = GetNormalizedFraction();
        int count = _tickCanvas.Children.Count;
        for (int i = 0; i < count; i++)
        {
            if (_tickCanvas.Children[i] is not Rectangle tick || tick.Tag is not double fraction)
                continue;
            bool isMajor = (i % 5 == 0);
            bool isActive = fraction <= currentFraction + 0.001;
            tick.Fill = isMajor
                ? (isActive ? ActiveMajorTickBrush : MajorTickBrush)
                : (isActive ? ActiveMinorTickBrush : MinorTickBrush);
        }
    }

    private void UpdateIndicatorPosition(bool immediate = false)
    {
        if (_indicator == null || _rootBorder == null) return;

        double width = _rootBorder.ActualWidth;
        double height = _rootBorder.ActualHeight;
        if (width <= 0 || height <= 0) return;
        if (Maximum <= Minimum) return;

        double usableWidth = width - TrackEdgePad * 2.0;
        if (usableWidth <= 0) return;

        double fraction = GetNormalizedFraction();
        double fillEdgeX = width * fraction;
        double clampedCenterX = Math.Clamp(fillEdgeX, TrackEdgePad, width - TrackEdgePad);
        double targetLeft = clampedCenterX - _indicator.Width / 2;
        double currentLeft = Canvas.GetLeft(_indicator);

        Canvas.SetTop(_indicator, (height - _indicator.Height) / 2);

        if (immediate || double.IsNaN(currentLeft) || double.IsInfinity(currentLeft) || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            _indicator.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(_indicator, targetLeft);
            return;
        }

        int durMs = _isDragging ? 75 : 170;
        var anim = new DoubleAnimation(targetLeft, TimeSpan.FromMilliseconds(durMs))
        {
            EasingFunction = SmoothOutEase,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        _indicator.BeginAnimation(Canvas.LeftProperty, anim, HandoffBehavior.SnapshotAndReplace);
        Canvas.SetLeft(_indicator, targetLeft);
    }

    #endregion

    #region Fill

    private double ComputeTargetFillWidth(double width)
    {
        return width * GetNormalizedFraction();
    }

    private void SetFillImmediate()
    {
        if (_fillBorder == null || _rootBorder == null) return;
        if (Maximum <= Minimum) return;

        double width = _rootBorder.ActualWidth;
        if (width <= 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (_fillBorder == null || _rootBorder == null || Maximum <= Minimum) return;
                double w = _rootBorder.ActualWidth;
                if (w > 0)
                {
                    _fillBorder.BeginAnimation(WidthProperty, null);
                    _fillBorder.Width = ComputeTargetFillWidth(w);
                    _lastAnimatedValue = Value;
                    UpdateTickBrushes();
                    UpdateIndicatorPosition(immediate: true);
                }
            });
            return;
        }

        _fillBorder.BeginAnimation(WidthProperty, null);
        _fillBorder.Width = ComputeTargetFillWidth(width);
        _lastAnimatedValue = Value;
    }

    private void AnimateFillSpring()
    {
        AnimateFillSmooth();
    }

    private void AnimateFillSmooth()
    {
        if (_fillBorder == null || _rootBorder == null) return;
        if (Maximum <= Minimum) return;

        double width = _rootBorder.ActualWidth;
        if (width <= 0) return;

        double targetWidth = ComputeTargetFillWidth(width);
        double currentWidth = !double.IsNaN(_fillBorder.Width) && _fillBorder.ActualWidth <= 0
            ? _fillBorder.Width
            : _fillBorder.ActualWidth;

        if (double.IsNaN(currentWidth) || double.IsInfinity(currentWidth) || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            _fillBorder.BeginAnimation(WidthProperty, null);
            _fillBorder.Width = targetWidth;
            return;
        }

        int durMs = _isDragging ? 75 : 170;
        var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(durMs))
        {
            EasingFunction = SmoothOutEase,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        _fillBorder.BeginAnimation(WidthProperty, anim, HandoffBehavior.SnapshotAndReplace);
        _fillBorder.Width = targetWidth;
    }

    #endregion

    #region Text

    private void UpdateLabelText()
    {
        if (_labelText == null) return;
        _labelText.Text = Label;
    }

    private void UpdateDescriptionText()
    {
        if (_descText == null) return;
        _descText.Text = Description;
        _descText.Visibility = string.IsNullOrEmpty(Description)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateValueText()
    {
        if (_valueText == null) return;
        string unit = Unit;
        int val = (int)Value;
        if (val == 0 && unit == " fps")
            _valueText.Text = Loc.Get("settings.fps.auto");
        else
            _valueText.Text = string.IsNullOrEmpty(unit) ? val.ToString() : $"{val}{unit}";
    }

    #endregion

    #region Mouse Interaction

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (_rootBorder != null)
        {
            var pos = e.GetPosition(_rootBorder);
            if (pos.Y < -4 || pos.Y > _rootBorder.ActualHeight + 4)
                return;
        }

        _isDragging = true;
        _dragOverflow = 0;
        CaptureMouse();
        Focus();
        UpdateInteractiveStateVisuals();

        SetValueFromMouse(e);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_isDragging) return;
        SetValueFromMouse(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_isDragging) return;

        _isDragging = false;
        ReleaseMouseCapture();
        UpdateInteractiveStateVisuals();

        if (Math.Abs(_dragOverflow) > 0.5)
            AnimateRubberBandRelease();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_isDragging) return;

        _isDragging = false;
        UpdateInteractiveStateVisuals();
        if (Math.Abs(_dragOverflow) > 0.5)
            AnimateRubberBandRelease();
    }

    private static unsafe (double ScaleX, double ScaleY) ComputeOverscrollScale(double overflowPx, double usableWidth)
    {
        double sx = 1.0, sy = 1.0;
        bool reduced = AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        try
        {
            if (NativeComputeElasticSliderOverscroll(overflowPx, usableWidth, reduced ? (byte)1 : (byte)0, &sx, &sy) == 0)
            {
                return (sx, sy);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        if (reduced || Math.Abs(overflowPx) < 0.5)
            return (1.0, 1.0);

        double decay = Decay(Math.Abs(overflowPx), 60);
        return (1.0 + decay * 0.0006, 1.0 - decay * 0.0010);
    }

    private void SetValueFromMouse(MouseEventArgs e)
    {
        if (_rootBorder == null) return;

        var pos = e.GetPosition(_rootBorder);
        double width = _rootBorder.ActualWidth;
        if (width <= 0) return;

        double usableWidth = width - TrackEdgePad * 2.0;
        if (usableWidth <= 0) return;

        double fraction = (pos.X - TrackEdgePad) / usableWidth;

        if (fraction < 0)
        {
            _dragOverflow = fraction * usableWidth;
            fraction = 0;
            var (sx, sy) = ComputeOverscrollScale(_dragOverflow, usableWidth);
            AnimateScaleImmediate(sx, sy, stretchFromLeft: false);
        }
        else if (fraction > 1)
        {
            _dragOverflow = (fraction - 1) * usableWidth;
            fraction = 1;
            var (sx, sy) = ComputeOverscrollScale(_dragOverflow, usableWidth);
            AnimateScaleImmediate(sx, sy, stretchFromLeft: true);
        }
        else
        {
            _dragOverflow = 0;
            if (_rootScale != null)
            {
                _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _rootScale.ScaleX = 1.0;
                _rootScale.ScaleY = 1.0;
            }
            if (_rootBorder != null && !_isDragging)
                _rootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        double rawValue = Minimum + fraction * (Maximum - Minimum);
        rawValue = SnapValue(rawValue);
        Value = Math.Max(Minimum, Math.Min(Maximum, rawValue));
    }

    private double GetEffectiveTickStep()
    {
        if (TickFrequency > 0)
        {
            return TickFrequency;
        }

        return Maximum > Minimum ? (Maximum - Minimum) / 20.0 : 1.0;
    }

    private int GetTickCount()
    {
        if (Maximum <= Minimum)
        {
            return 1;
        }

        double step = GetEffectiveTickStep();
        if (step <= 0)
        {
            return 20;
        }

        return Math.Max(1, (int)Math.Round((Maximum - Minimum) / step));
    }

    private double SnapValue(double value)
    {
        double step = GetEffectiveTickStep();
        if (step <= 0)
        {
            return Math.Max(Minimum, Math.Min(Maximum, value));
        }

        double snapped = Math.Round((value - Minimum) / step) * step + Minimum;
        return Math.Max(Minimum, Math.Min(Maximum, snapped));
    }

    private static double Decay(double value, double max)
    {
        if (max == 0) return 0;
        double entry = value / max;
        double sigmoid = 2.0 * (1.0 / (1.0 + Math.Exp(-entry)) - 0.5);
        return sigmoid * max;
    }

    #endregion

    #region Scale Animations (rubber-band only)

    private void AnimateScaleImmediate(double scaleX, double scaleY, bool stretchFromLeft)
    {
        if (_rootScale == null || _rootBorder == null) return;
        _rootBorder.RenderTransformOrigin = stretchFromLeft
            ? new Point(0, 0.5)
            : new Point(1, 0.5);

        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _rootScale.ScaleX = scaleX;
        _rootScale.ScaleY = scaleY;
    }

    private void AnimateRubberBandRelease()
    {
        if (_rootScale == null || _rootBorder == null) return;
        var dur = TimeSpan.FromMilliseconds(260);
        var ax = new DoubleAnimation(1.0, dur) { EasingFunction = SmoothOutEase };
        var ay = new DoubleAnimation(1.0, dur) { EasingFunction = SmoothOutEase };
        Timeline.SetDesiredFrameRate(ax, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(ay, AnimationConfig.TargetFps);
        ax.Completed += (_, _) =>
        {
            if (!_isDragging) _rootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    #endregion

    #region Keyboard Navigation

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        double step = GetEffectiveTickStep();

        bool handled = false;
        switch (e.Key)
        {
            case Key.Left:
                Value = Math.Clamp(Value + (FlowDirection == FlowDirection.RightToLeft ? step : -step), Minimum, Maximum);
                handled = true;
                break;
            case Key.Down:
                Value = Math.Max(Minimum, Value - step);
                handled = true;
                break;
            case Key.Right:
                Value = Math.Clamp(Value + (FlowDirection == FlowDirection.RightToLeft ? -step : step), Minimum, Maximum);
                handled = true;
                break;
            case Key.Up:
                Value = Math.Min(Maximum, Value + step);
                handled = true;
                break;
            case Key.PageDown:
                Value = Math.Max(Minimum, Value - step * 5);
                handled = true;
                break;
            case Key.PageUp:
                Value = Math.Min(Maximum, Value + step * 5);
                handled = true;
                break;
            case Key.Home:
                Value = Minimum;
                handled = true;
                break;
            case Key.End:
                Value = Maximum;
                handled = true;
                break;
        }

        if (handled)
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    #endregion
}
