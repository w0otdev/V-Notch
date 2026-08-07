using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace VNotch.Controls;

public class ElasticSlider : Slider
{
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
        if (d is ElasticSlider s) s.UpdateLabelText();
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ElasticSlider s) s.UpdateDescriptionText();
    }

    private static void OnUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ElasticSlider s) s.UpdateValueText();
    }

    #endregion

    private Border? _rootBorder;
    private Border? _fillBorder;
    private Canvas? _tickCanvas;
    private Rectangle? _indicator;
    private TextBlock? _labelText;
    private TextBlock? _descText;
    private TextBlock? _valueText;
    private ScaleTransform? _rootScale;
    private bool _isDragging;
    private double _dragOverflow;
    private double _lastAnimatedValue = double.NaN;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _rootBorder = Template?.FindName("PART_Root", this) as Border;
        _fillBorder = Template?.FindName("PART_Fill", this) as Border;
        _tickCanvas = Template?.FindName("PART_Ticks", this) as Canvas;
        _indicator = Template?.FindName("PART_Indicator", this) as Rectangle;
        _labelText = Template?.FindName("PART_Label", this) as TextBlock;
        _descText = Template?.FindName("PART_Description", this) as TextBlock;
        _valueText = Template?.FindName("PART_Value", this) as TextBlock;

        if (_rootBorder != null)
        {
            _rootScale = new ScaleTransform(1, 1);
            _rootBorder.RenderTransform = _rootScale;
            _rootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
            _rootBorder.SizeChanged += (_, _) =>
            {
                SetFillImmediate();
                DrawTicks();
                UpdateIndicatorPosition();
            };
        }

        SetFillImmediate();
        UpdateLabelText();
        UpdateDescriptionText();
        UpdateValueText();
        DrawTicks();
        UpdateIndicatorPosition();
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
        UpdateIndicatorPosition();

        if (IsSnapToTickEnabled && TickFrequency > 0 && !double.IsNaN(_lastAnimatedValue) && oldValue != newValue)
            AnimateFillSpring();
        else
            AnimateFillSmooth();

        _lastAnimatedValue = newValue;
    }

    protected override void OnMinimumChanged(double oldMinimum, double newMinimum)
    {
        base.OnMinimumChanged(oldMinimum, newMinimum);
        SetFillImmediate();
        DrawTicks();
        UpdateIndicatorPosition();
    }

    protected override void OnMaximumChanged(double oldMaximum, double newMaximum)
    {
        base.OnMaximumChanged(oldMaximum, newMaximum);
        SetFillImmediate();
        DrawTicks();
        UpdateIndicatorPosition();
    }

    #region Tick Marks & Indicator

    private double GetRightReserve()
    {
        if (_valueText == null) return 62;
        _valueText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Max(50, _valueText.DesiredSize.Width + 22);
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

        double leftPad = 12;
        double rightReserve = GetRightReserve();
        double usableWidth = width - leftPad - rightReserve;
        if (usableWidth <= 0) return;

        for (int i = 0; i <= totalTicks; i++)
        {
            double fraction = (double)i / totalTicks;

            double x = leftPad + fraction * usableWidth;

            bool isMajor = (i % 5 == 0);

            double tickHeight = isMajor ? 14 : 8;
            double tickWidth = isMajor ? 2 : 1.2;
            byte alpha = isMajor ? (byte)70 : (byte)35;

            var tick = new Rectangle
            {
                Width = tickWidth,
                Height = tickHeight,
                RadiusX = tickWidth / 2,
                RadiusY = tickWidth / 2,
                Fill = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)),
                SnapsToDevicePixels = true
            };

            Canvas.SetLeft(tick, x - tickWidth / 2);
            Canvas.SetTop(tick, (height - tickHeight) / 2);
            _tickCanvas.Children.Add(tick);
        }
    }

    private void UpdateIndicatorPosition()
    {
        if (_indicator == null || _rootBorder == null) return;

        double width = _rootBorder.ActualWidth;
        double height = _rootBorder.ActualHeight;
        if (width <= 0 || height <= 0) return;
        if (Maximum <= Minimum) return;

        double leftPad = 12;
        double rightReserve = GetRightReserve();
        double usableWidth = width - leftPad - rightReserve;
        if (usableWidth <= 0) return;

        double fraction = (Value - Minimum) / (Maximum - Minimum);
        double x = leftPad + fraction * usableWidth;
        double targetLeft = x - _indicator.Width / 2;
        double currentLeft = Canvas.GetLeft(_indicator);
        if (double.IsNaN(currentLeft) || double.IsInfinity(currentLeft))
        {
            _indicator.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(_indicator, targetLeft);
            return;
        }

        if (_isDragging)
        {
            var anim = new DoubleAnimation(currentLeft, targetLeft, TimeSpan.FromMilliseconds(50))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
            _indicator.BeginAnimation(Canvas.LeftProperty, anim);
        }
        else
        {
            var anim = new DoubleAnimation(currentLeft, targetLeft, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 7 }
            };
            Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
            _indicator.BeginAnimation(Canvas.LeftProperty, anim);
        }
    }

    #endregion

    #region Fill

    private void SetFillImmediate()
    {
        if (_fillBorder == null || _rootBorder == null) return;
        if (Maximum <= Minimum) return;

        double width = _rootBorder.ActualWidth;
        if (width <= 0)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                double w = _rootBorder.ActualWidth;
                if (w > 0)
                {
                    double f = (Value - Minimum) / (Maximum - Minimum);
                    _fillBorder.BeginAnimation(WidthProperty, null);
                    _fillBorder.Width = w * Math.Max(0, Math.Min(1, f));
                    _lastAnimatedValue = Value;
                }
            });
            return;
        }

        double fraction = (Value - Minimum) / (Maximum - Minimum);
        _fillBorder.BeginAnimation(WidthProperty, null);
        _fillBorder.Width = width * Math.Max(0, Math.Min(1, fraction));
        _lastAnimatedValue = Value;
    }

    private void AnimateFillSpring()
    {
        if (_fillBorder == null || _rootBorder == null) return;
        if (Maximum <= Minimum) return;

        double width = _rootBorder.ActualWidth;
        if (width <= 0) return;

        double fraction = (Value - Minimum) / (Maximum - Minimum);
        double targetWidth = width * Math.Max(0, Math.Min(1, fraction));
        double currentWidth = _fillBorder.Width;
        if (double.IsNaN(currentWidth) || double.IsInfinity(currentWidth))
        {
            currentWidth = _fillBorder.ActualWidth;
        }
        if (double.IsNaN(currentWidth) || double.IsInfinity(currentWidth))
        {
            _fillBorder.BeginAnimation(WidthProperty, null);
            _fillBorder.Width = targetWidth;
            return;
        }

        var anim = new DoubleAnimation(currentWidth, targetWidth, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 7 }
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        _fillBorder.BeginAnimation(WidthProperty, anim);
    }

    private void AnimateFillSmooth()
    {
        if (_fillBorder == null || _rootBorder == null) return;
        if (Maximum <= Minimum) return;

        double width = _rootBorder.ActualWidth;
        if (width <= 0) return;

        double fraction = (Value - Minimum) / (Maximum - Minimum);
        double targetWidth = width * Math.Max(0, Math.Min(1, fraction));
        double currentWidth = _fillBorder.Width;
        if (double.IsNaN(currentWidth) || double.IsInfinity(currentWidth))
        {
            currentWidth = _fillBorder.ActualWidth;
        }
        if (double.IsNaN(currentWidth) || double.IsInfinity(currentWidth))
        {
            _fillBorder.BeginAnimation(WidthProperty, null);
            _fillBorder.Width = targetWidth;
            return;
        }

        if (_isDragging)
        {
            var anim = new DoubleAnimation(currentWidth, targetWidth, TimeSpan.FromMilliseconds(50))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
            _fillBorder.BeginAnimation(WidthProperty, anim);
        }
        else
        {
            AnimateFillSpring();
        }
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
            _valueText.Text = "Auto";
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
            if (pos.Y < 0 || pos.Y > _rootBorder.ActualHeight)
                return;
        }

        _isDragging = true;
        _dragOverflow = 0;
        CaptureMouse();
        Focus();

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

        if (Math.Abs(_dragOverflow) > 1)
            AnimateRubberBandRelease();
    }

    private void SetValueFromMouse(MouseEventArgs e)
    {
        if (_rootBorder == null) return;

        var pos = e.GetPosition(_rootBorder);
        double width = _rootBorder.ActualWidth;
        if (width <= 0) return;

        double leftPad = 12;
        double rightReserve = GetRightReserve();
        double usableWidth = width - leftPad - rightReserve;
        if (usableWidth <= 0) return;

        double fraction = (pos.X - leftPad) / usableWidth;

        if (fraction < 0)
        {
            _dragOverflow = fraction * usableWidth;
            fraction = 0;
            double decay = Decay(Math.Abs(_dragOverflow), 60);
            AnimateScaleImmediate(1.0 + decay * 0.001, 1.0 - decay * 0.002, stretchFromLeft: false);
        }
        else if (fraction > 1)
        {
            _dragOverflow = (fraction - 1) * usableWidth;
            fraction = 1;
            double decay = Decay(Math.Abs(_dragOverflow), 60);
            AnimateScaleImmediate(1.0 + decay * 0.001, 1.0 - decay * 0.002, stretchFromLeft: true);
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
            if (_rootBorder != null)
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
        var dur = TimeSpan.FromMilliseconds(600);
        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 };
        var ax = new DoubleAnimation(1.0, dur) { EasingFunction = ease };
        var ay = new DoubleAnimation(1.0, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(ax, VNotch.Services.AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(ay, VNotch.Services.AnimationConfig.TargetFps);
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);

        ax.Completed += (_, _) =>
        {
            _rootBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        };
    }

    #endregion

    #region Keyboard Navigation

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = GetEffectiveTickStep();

        bool handled = false;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                Value = Math.Max(Minimum, Value - step);
                handled = true;
                break;
            case Key.Right:
            case Key.Up:
                Value = Math.Min(Maximum, Value + step);
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
            e.Handled = true;

        base.OnKeyDown(e);
    }

    #endregion
}
