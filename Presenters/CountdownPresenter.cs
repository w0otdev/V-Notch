using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Presenters;

public sealed class CountdownPresenter : IDisposable
{
    private static readonly Geometry _playGeometry = CreateFrozenGeometry(
        "M133,440a35.37,35.37,0,0,1-17.5-4.67c-12-6.8-17.46-20-17.46-41.73V118.4c0-21.74,5.48-34.93,17.46-41.73a35.13,35.13,0,0,1,35.77.45L399.68,225.11a38.19,38.19,0,0,1,0,61.78L151.23,435a35.77,35.77,0,0,1-18.27,5Z");
    private static readonly Geometry _pauseGeometry = CreateFrozenGeometry(
        "M224,320a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Zm96,0a16,16,0,0,1-32,0V192a16,16,0,0,1,32,0Z");

    private static readonly Brush _startIdleBrush = CreateFrozenVerticalGradient(
        Color.FromRgb(0xFF, 0xA0, 0x33), Color.FromRgb(0xFF, 0x7A, 0x00));
    private static readonly Brush _startRunningBrush = CreateFrozenVerticalGradient(
        Color.FromRgb(0xE0, 0x8A, 0x1E), Color.FromRgb(0xC2, 0x64, 0x00));

    private static readonly Color _borderIdleColor = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
    private static readonly Color _borderEditingColor = Color.FromArgb(0x8C, 0xFF, 0x8C, 0x00);
    private static readonly Color _borderFlashColor = Color.FromArgb(0x70, 0xFF, 0x8C, 0x00);
    private static readonly Color _borderErrorColor = Color.FromArgb(0xB4, 0xFF, 0x45, 0x3A);
    private static readonly Color _digitsRestColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color _digitsFlashColor = Color.FromRgb(0xFF, 0xC9, 0x85);

    private static readonly Duration _dur200 = new(TimeSpan.FromMilliseconds(200));
    private static readonly IEasingFunction _easeQuadOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    private const int RepeatInitialDelayMs = 400;
    private const int RepeatFastIntervalMs = 80;
    private const int RepeatAccelerateAfter = 4;

    private readonly CountdownViewRefs _refs;
    private readonly Dispatcher _dispatcher;

    private DispatcherTimer? _repeatTimer;
    private Action? _repeatAction;
    private int _repeatCount;
    private bool _disposed;

    public CountdownPresenter(CountdownViewRefs refs, Dispatcher dispatcher)
    {
        _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void SetStartVisual(bool running)
    {
        if (_disposed) return;

        _refs.StartIcon.Data = running ? _pauseGeometry : _playGeometry;
        _refs.StartButton.Background = running ? _startRunningBrush : _startIdleBrush;

        _refs.StepperCapsule.IsHitTestVisible = !running;
        var stepperFade = MakeAnim(running ? 0.4 : 1.0, _dur200, _easeQuadOut);
        _refs.StepperCapsule.BeginAnimation(UIElement.OpacityProperty, stepperFade);
    }

    public void StartRepeat(Action stepAction)
    {
        if (_disposed) return;

        _repeatAction = stepAction;
        _repeatCount = 0;

        _repeatTimer?.Stop();
        _repeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(RepeatInitialDelayMs)
        };
        _repeatTimer.Tick += (s, e) =>
        {
            _repeatCount++;
            if (_repeatCount >= RepeatAccelerateAfter && _repeatTimer.Interval.TotalMilliseconds > RepeatFastIntervalMs)
            {
                _repeatTimer.Interval = TimeSpan.FromMilliseconds(RepeatFastIntervalMs);
            }
            _repeatAction?.Invoke();
        };
        _repeatTimer.Start();
    }

    public void StopRepeat()
    {
        _repeatTimer?.Stop();
        _repeatTimer = null;
        _repeatAction = null;
        _repeatCount = 0;
    }

    public void AnimateDigitBump(double magnitude = 1.0)
    {
        if (_disposed || AnimationConfig.ReduceMotion) return;

        _refs.DisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _refs.DisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _refs.DisplayScale.ScaleX = 1.0;
        _refs.DisplayScale.ScaleY = 1.0;

        double targetScale = 1.0 + 0.05 * Math.Clamp(magnitude, 0.4, 2.5);
        var durUp = TimeSpan.FromMilliseconds(65);
        var durDown = TimeSpan.FromMilliseconds(160);

        var upX = MakeAnim(1.0, targetScale, new Duration(durUp), _easeQuadOut);
        var upY = MakeAnim(1.0, targetScale, new Duration(durUp), _easeQuadOut);
        var settle = MakeAnim(targetScale, 1.0, new Duration(durDown), _easeQuadOut);

        upX.Completed += (_, _) => _refs.DisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, settle);
        upY.Completed += (_, _) => _refs.DisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, settle);

        _refs.DisplayScale.BeginAnimation(ScaleTransform.ScaleXProperty, upX);
        _refs.DisplayScale.BeginAnimation(ScaleTransform.ScaleYProperty, upY);

        AnimateDigitFlash();
        AnimatePanelBorderFlash();
    }

    public void AnimateDigitFlash()
    {
        if (_disposed || AnimationConfig.ReduceMotion) return;

        var toAmber = new ColorAnimation(_digitsRestColor, _digitsFlashColor, new Duration(TimeSpan.FromMilliseconds(70)))
        {
            EasingFunction = _easeQuadOut
        };
        var toRest = new ColorAnimation(_digitsFlashColor, _digitsRestColor, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = _easeQuadOut
        };
        toAmber.Completed += (_, _) => _refs.DigitsBrush.BeginAnimation(SolidColorBrush.ColorProperty, toRest);
        _refs.DigitsBrush.BeginAnimation(SolidColorBrush.ColorProperty, toAmber);
    }

    public void AnimatePanelBorder(Color target, int durationMs)
    {
        if (_disposed) return;

        var anim = new ColorAnimation(target, new Duration(TimeSpan.FromMilliseconds(durationMs)))
        {
            EasingFunction = _easeQuadOut
        };
        _refs.PanelBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    public void AnimatePanelBorderFlash()
    {
        if (_disposed || AnimationConfig.ReduceMotion) return;

        var flash = new ColorAnimation(_borderIdleColor, _borderFlashColor, new Duration(TimeSpan.FromMilliseconds(70)))
        {
            EasingFunction = _easeQuadOut
        };
        flash.Completed += (_, _) => AnimatePanelBorder(_borderIdleColor, 200);
        _refs.PanelBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, flash);
    }

    public void SetHighlight(UIElement highlight, bool visible)
    {
        if (_disposed) return;

        var anim = MakeAnim(visible ? 0.0 : 1.0, visible ? 1.0 : 0.0,
            new Duration(TimeSpan.FromMilliseconds(visible ? 120 : 180)), _easeQuadOut);
        highlight.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateFrozenVerticalGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRepeat();
    }
}
