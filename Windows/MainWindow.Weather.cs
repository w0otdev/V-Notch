using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Weather Widget

    private WeatherPresenter? _weatherPresenter;
    private bool _hasWeatherData;
    private int _weatherRevealVersion;

    internal void InitializeWeatherWidget()
    {
        if (WeatherWidgetContent != null)
        {
            WeatherWidgetContent.IsVisibleChanged += (s, e) =>
            {
                if (WeatherWidgetContent.Visibility == Visibility.Visible)
                {
                    if (!_hasWeatherData)
                    {
                        UpdateWeatherSkeletonState();
                    }
                }
                else
                {
                    StopWeatherSkeletonAnimation();
                }
            };
        }

        if (!_settings.EnableWeather)
        {
            ShowWeatherStatus(isEnabled: false);
        }
        else
        {
            UpdateWeatherSkeletonState();
        }
    }

    internal void InitializeWeatherPresenter()
    {
        if (_weatherPresenter != null) return;

        var refs = new WeatherViewRefs(
            WeatherLocationText,
            WeatherTempText,
            WeatherConditionText,
            WeatherHiLoText);

        _weatherPresenter = new WeatherPresenter(_weatherModule, new DispatcherService(Dispatcher), refs);
    }

    internal void DisposeWeatherPresenter()
    {
        _weatherPresenter?.Dispose();
        _weatherPresenter = null;
    }

    private void WeatherModule_WeatherUpdated(object? sender, WeatherUpdateEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            var weather = e.Weather;
            if (weather == null)
            {
                ShowWeatherStatus(_settings.EnableWeather);
                return;
            }

            UpdateWeatherUI(weather);
        });
    }

    private void StartWeatherSkeletonAnimation()
    {
        if (WeatherWidgetSkeleton == null) return;
        if (WeatherWidgetSkeleton.Visibility != Visibility.Visible) return;

        var anim = new DoubleAnimation(0.55, 1.0, new Duration(TimeSpan.FromMilliseconds(950)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        WeatherWidgetSkeleton.BeginAnimation(OpacityProperty, anim);
    }

    private void StopWeatherSkeletonAnimation()
    {
        if (WeatherWidgetSkeleton == null) return;
        WeatherWidgetSkeleton.BeginAnimation(OpacityProperty, null);
    }

    internal void StartShelfWeatherSkeletonAnimation()
    {
        if (ShelfWeatherSkeleton == null) return;
        if (ShelfWeatherSkeleton.Visibility != Visibility.Visible) return;

        var anim = new DoubleAnimation(0.55, 1.0, new Duration(TimeSpan.FromMilliseconds(950)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        ShelfWeatherSkeleton.BeginAnimation(OpacityProperty, anim);
    }

    internal void StopShelfWeatherSkeletonAnimation()
    {
        if (ShelfWeatherSkeleton == null) return;
        ShelfWeatherSkeleton.BeginAnimation(OpacityProperty, null);
    }

    private void UpdateWeatherSkeletonState()
    {
        if (_hasWeatherData)
        {
            StopWeatherSkeletonAnimation();
            if (WeatherWidgetSkeleton != null) WeatherWidgetSkeleton.Visibility = Visibility.Collapsed;
            if (WeatherActualContent != null)
            {
                WeatherActualContent.Visibility = Visibility.Visible;
                WeatherActualContent.Opacity = 1;
            }
            if (WeatherWidgetTranslate != null) WeatherWidgetTranslate.Y = 0;
        }
        else
        {
            if (WeatherActualContent != null)
            {
                WeatherActualContent.Visibility = Visibility.Collapsed;
                WeatherActualContent.Opacity = 0;
            }
            if (WeatherWidgetSkeleton != null)
            {
                WeatherWidgetSkeleton.Visibility = Visibility.Visible;
                StartWeatherSkeletonAnimation();
            }
        }
    }

    private void ShowWeatherStatus(bool isEnabled)
    {
        _hasWeatherData = false;
        _weatherRevealVersion++;
        StopWeatherSkeletonAnimation();
        if (WeatherWidgetSkeleton != null)
            WeatherWidgetSkeleton.Visibility = Visibility.Collapsed;

        if (WeatherActualContent != null)
        {
            WeatherActualContent.BeginAnimation(OpacityProperty, null);
            WeatherActualContent.Visibility = Visibility.Visible;
            WeatherActualContent.Opacity = 1;
        }
        if (WeatherWidgetTranslate != null)
        {
            WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            WeatherWidgetTranslate.Y = 0;
        }

        WeatherLocationText.Text = Loc.Get(isEnabled ? "weather.unavailable" : "weather.disabled");
        WeatherTempText.Text = "\u2014\u00b0";
        WeatherConditionText.Text = Loc.Get(isEnabled ? "weather.retryLater" : "weather.enableInSettings");
        WeatherHiLoText.Text = string.Empty;

        RefreshShelfWeatherData();
    }

    private void UpdateWeatherUI(WeatherInfo weather)
    {
        bool shouldReveal = !_hasWeatherData;
        WeatherLocationText.Text = string.IsNullOrWhiteSpace(weather.City) ? "\u2014" : weather.City;
        WeatherTempText.Text = $"{weather.Temperature}\u00b0";
        WeatherConditionText.Text = WeatherConditionFormatter.Format(weather.WeatherCode);
        WeatherHiLoText.Text = Loc.Get("weather.highLow", weather.High, weather.Low);
        _hasWeatherData = true;

        if (shouldReveal)
            RevealWeatherContent();
        else
            UpdateWeatherSkeletonState();

        RefreshShelfWeatherData();
    }

    private void RevealWeatherContent()
    {
        int transitionVersion = ++_weatherRevealVersion;

        StopWeatherSkeletonAnimation();
        if (WeatherWidgetSkeleton != null)
        {
            WeatherWidgetSkeleton.Visibility = Visibility.Collapsed;
        }

        if (WeatherActualContent == null || WeatherWidgetTranslate == null) return;

        WeatherActualContent.Visibility = Visibility.Visible;
        WeatherActualContent.BeginAnimation(OpacityProperty, null);
        WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        if (AnimationConfig.ReduceMotion)
        {
            WeatherActualContent.Opacity = 1;
            WeatherWidgetTranslate.Y = 0;
            return;
        }

        WeatherActualContent.Opacity = 0;
        WeatherWidgetTranslate.Y = 8;

        var duration = new Duration(TimeSpan.FromMilliseconds(420));
        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var slideIn = new DoubleAnimation(8, 0, duration) { EasingFunction = ease };

        fadeIn.Completed += (_, _) =>
        {
            if (!_hasWeatherData || transitionVersion != _weatherRevealVersion)
                return;

            WeatherActualContent.BeginAnimation(OpacityProperty, null);
            WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            WeatherActualContent.Opacity = 1;
            WeatherWidgetTranslate.Y = 0;
        };

        Timeline.SetDesiredFrameRate(fadeIn, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slideIn, AnimationConfig.TargetFps);
        WeatherActualContent.BeginAnimation(OpacityProperty, fadeIn);
        WeatherWidgetTranslate.BeginAnimation(TranslateTransform.YProperty, slideIn);
    }

    #endregion
}
