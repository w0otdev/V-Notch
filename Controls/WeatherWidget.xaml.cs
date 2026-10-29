using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;

namespace VNotch.Controls;

public sealed partial class WeatherWidget : UserControl, IDisposable
{
    private WeatherModule? _module;
    private bool _enabled;
    private bool _hasWeatherData;
    private bool _disposed;
    private int _revealVersion;

    public WeatherWidget()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibilityChanged;
    }

    public void Attach(WeatherModule module, bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(module);
        if (_module != null) _module.WeatherUpdated -= OnWeatherUpdated;
        _module = module;
        _module.WeatherUpdated += OnWeatherUpdated;
        ApplySettings(enabled);
    }

    public void ApplySettings(bool enabled)
    {
        if (_disposed) return;
        bool wasEnabled = _enabled;
        _enabled = enabled;
        if (!enabled)
            ShowStatus();
        else if (!wasEnabled)
            ShowLoading();
    }

    public bool RetryIfUnavailable()
    {
        if (_disposed || !_enabled || _hasWeatherData || _module == null) return false;
        ShowLoading();
        _module.RetryNow();
        return true;
    }

    private void OnWeatherUpdated(object? sender, WeatherUpdateEventArgs e)
    {
        if (_disposed || Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess())
            Present(e.Weather);
        else
            Dispatcher.BeginInvoke(() => Present(e.Weather));
    }

    internal void Present(WeatherInfo? weather)
    {
        if (_disposed) return;
        if (!_enabled || weather == null)
        {
            ShowStatus();
            return;
        }

        bool shouldReveal = !_hasWeatherData;
        WeatherLocationText.Text = string.IsNullOrWhiteSpace(weather.City) ? "—" : weather.City;
        WeatherTempText.Text = $"{weather.Temperature}°";
        WeatherConditionText.Text = WeatherConditionFormatter.Format(weather.WeatherCode);
        WeatherHiLoText.Text = Loc.Get("weather.highLow", weather.High, weather.Low);
        _hasWeatherData = true;
        if (shouldReveal) RevealContent();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        StopSkeletonAnimation();
        if (IsVisible && WeatherWidgetSkeleton.Visibility == Visibility.Visible)
            StartSkeletonAnimation();
    }

    private void ShowLoading()
    {
        _hasWeatherData = false;
        ++_revealVersion;
        ClearContentAnimations();
        WeatherActualContent.Visibility = Visibility.Collapsed;
        WeatherActualContent.Opacity = 0;
        WeatherWidgetSkeleton.Visibility = Visibility.Visible;
        if (IsVisible) StartSkeletonAnimation();
    }

    private void StartSkeletonAnimation()
    {
        var animation = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(950))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        WeatherWidgetSkeleton.BeginAnimation(OpacityProperty, animation);
    }

    private void StopSkeletonAnimation() => WeatherWidgetSkeleton.BeginAnimation(OpacityProperty, null);

    private void ClearContentAnimations()
    {
        WeatherActualContent.BeginAnimation(OpacityProperty, null);
        WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void ShowStatus()
    {
        _hasWeatherData = false;
        ++_revealVersion;
        StopSkeletonAnimation();
        ClearContentAnimations();
        WeatherWidgetSkeleton.Visibility = Visibility.Collapsed;
        WeatherActualContent.Visibility = Visibility.Visible;
        WeatherActualContent.Opacity = 1;
        WeatherWidgetTranslate.Y = 0;
        WeatherLocationText.Text = Loc.Get(_enabled ? "weather.unavailable" : "weather.disabled");
        WeatherTempText.Text = "—°";
        WeatherConditionText.Text = Loc.Get(_enabled ? "weather.retryLater" : "weather.enableInSettings");
        WeatherHiLoText.Text = string.Empty;
    }

    private void RevealContent()
    {
        int version = ++_revealVersion;
        StopSkeletonAnimation();
        ClearContentAnimations();
        WeatherWidgetSkeleton.Visibility = Visibility.Collapsed;
        WeatherActualContent.Visibility = Visibility.Visible;
        if (AnimationConfig.ReduceMotion || !IsVisible)
        {
            WeatherActualContent.Opacity = 1;
            WeatherWidgetTranslate.Y = 0;
            return;
        }

        WeatherActualContent.Opacity = 0;
        WeatherWidgetTranslate.Y = 8;
        var duration = new Duration(TimeSpan.FromMilliseconds(420));
        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var slide = new DoubleAnimation(8, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (_disposed || !_hasWeatherData || version != _revealVersion) return;
            ClearContentAnimations();
            WeatherActualContent.Opacity = 1;
            WeatherWidgetTranslate.Y = 0;
        };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
        WeatherActualContent.BeginAnimation(OpacityProperty, fade);
        WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_revealVersion;
        if (_module != null) _module.WeatherUpdated -= OnWeatherUpdated;
        _module = null;
        IsVisibleChanged -= OnVisibilityChanged;
        StopSkeletonAnimation();
        ClearContentAnimations();
    }
}
