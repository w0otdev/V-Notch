using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private Storyboard? _chargingPulseStoryboard;
    private bool _chargingPulseWanted;
    private bool? _wasPluggedIn = null;
    private bool _isChargingNotificationVisible = false;
    private int _chargingGlanceToken = 0;
    private DispatcherTimer? _chargingNotificationDismissTimer;

    private bool AmbientAnimationsAllowed =>
        !AnimationConfig.ReduceMotion && _isNotchVisible && !_isHiddenByFullscreen;

    private void RefreshAmbientAnimations()
    {
        bool allowed = AmbientAnimationsAllowed;

        if (_chargingPulseWanted && allowed) StartChargingPulse();
        else StopChargingPulseInternal();

        if (_shimmerWanted && allowed) StartTitleShimmer();
        else StopTitleShimmerInternal();
    }

    private void OnReduceMotionChanged()
    {
        void ApplyMotionPreference()
        {
            RefreshAmbientAnimations();
            if (!AnimationConfig.ReduceMotion) return;
            SetVolumeFill(VolumeBarScale, (double)VolumeBarScale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
            SetVolumeFill(VolumeIndicatorScale, (double)VolumeIndicatorScale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
            _volumeScrollSyncAfterUtc = DateTime.MinValue;
        }
        if (Dispatcher.CheckAccess()) ApplyMotionPreference();
        else Dispatcher.BeginInvoke(new Action(ApplyMotionPreference));
    }

    private void HandleBatteryUpdate(BatteryInfo battery)
    {
        AnimationConfig.SetReduceMotion(battery.IsBatterySaver);

        const double batteryFillWidth = 22.8;
        double targetWidth = Math.Clamp(battery.Percentage / 100.0 * batteryFillWidth, 1.08, batteryFillWidth);
        double targetScale = targetWidth / batteryFillWidth;
        var scaleAnimation = new DoubleAnimation
        {
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleAnimation, VNotch.Services.AnimationConfig.TargetFps);
        BatteryFillScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);

        SolidColorBrush fillBrush;
        SolidColorBrush percentBrush;
        bool showLightning = false;

        if (battery.HasBattery && battery.Percentage >= 0 && battery.Percentage <= 20 && !battery.IsPowerConnected)
        {
            fillBrush = _brushLowBattery;
            percentBrush = _brushLowBattery;
            showLightning = false;
        }
        else if (battery.IsPowerConnected)
        {
            fillBrush = _brushCharging;
            percentBrush = VNotch.Services.UiPalette.PrimaryBrush;
            showLightning = true;
        }
        else
        {
            fillBrush = _brushWhite;
            percentBrush = VNotch.Services.UiPalette.PrimaryBrush;
            showLightning = false;
        }

        AnimateBrushTransition(BatteryFill, fillBrush);
        AnimateBrushTransition(BatteryPercent, percentBrush);
        AnimateChargingBolt(showLightning);

        if (battery.IsCharging)
        {
            StartChargingPulse();
        }
        else
        {
            StopChargingPulse();
        }

        if (battery.IsPowerConnected && _wasPluggedIn == false)
        {
            ShowChargingGlance(battery, ChargingGlanceKind.PluggedIn);
        }
        else if (_wasPluggedIn == true && !battery.IsPowerConnected)
        {
            ShowChargingGlance(battery, ChargingGlanceKind.Unplugged);
        }
        _wasPluggedIn = battery.IsPowerConnected;
    }

    private enum ChargingGlanceKind
    {
        PluggedIn,
        Unplugged
    }

    private void ShowChargingGlance(BatteryInfo battery, ChargingGlanceKind kind)
    {
        if (_isExpanded || _isAnimating || _isGreetingActive)
            return;

        if (!TryAcquireCompactSlot(VNotch.Controllers.CompactPillSlot.Charging, out int token))
            return;

        _chargingGlanceToken = token;
        _isChargingNotificationVisible = true;

        ChargingPercentText.Text = battery.GetPercentageText();

        Color accent;
        string statusKey;
        if (kind == ChargingGlanceKind.PluggedIn)
        {
            if (battery.IsFullyCharged)
            {
                statusKey = "battery.fullyCharged";
                accent = Color.FromRgb(0x30, 0xD1, 0x58);
            }
            else
            {
                statusKey = "battery.charging";
                accent = Color.FromRgb(0x30, 0xD1, 0x58);
            }
        }
        else
        {
            statusKey = "battery.onBattery";
            accent = Color.FromRgb(0xFF, 0x95, 0x00);
        }
        ChargingStatusText.Text = Loc.Get(statusKey);
        ChargingPercentText.Foreground = new SolidColorBrush(accent);
        ChargingBatteryFill.Background = new SolidColorBrush(accent);

        if (battery.HasPowerRate && Math.Abs(battery.PowerWatts) >= 0.1)
        {
            double watts = Math.Abs(battery.PowerWatts);
            string formatted = watts >= 10
                ? $"{watts:0} W"
                : $"{watts:0.0} W";
            ChargingWattText.Text = formatted;
            ChargingWattText.Visibility = Visibility.Visible;
        }
        else
        {
            ChargingWattText.Visibility = Visibility.Collapsed;
            ChargingWattText.Text = string.Empty;
        }

        double fillWidth = Math.Max(2, battery.Percentage / 100.0 * 17.0);
        ChargingBatteryFill.Width = fillWidth;

        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;

        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;

        ChargingNotification.Visibility = Visibility.Visible;
        ChargingNotification.Opacity = 0;

        PlayChargingBounce();

        var fadeIn = MakeCompactNotificationEntrance(0, 1);
        ChargingNotification.BeginAnimation(OpacityProperty, fadeIn);

        var slideUp = MakeCompactNotificationEntrance(6, 0);
        ChargingNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);

        var iconScale = MakeAnim(0.6d, 1d, _dur400, _easeSpring, TimeSpan.FromMilliseconds(150));
        ChargingIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconScale);
        ChargingIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconScale);

        StopChargingNotificationDismissTimer();

        int dismissToken = _chargingGlanceToken;
        var dismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(3000)
        };
        _chargingNotificationDismissTimer = dismissTimer;
        dismissTimer.Tick += (s, e) =>
        {
            dismissTimer.Stop();
            if (!ReferenceEquals(_chargingNotificationDismissTimer, dismissTimer) ||
                dismissToken != _chargingGlanceToken)
            {
                return;
            }

            _chargingNotificationDismissTimer = null;
            DismissChargingNotification();
        };
        dismissTimer.Start();
    }

    private void DismissChargingNotification()
    {
        if (!_isChargingNotificationVisible) return;

        int token = _chargingGlanceToken;

        AnimateCompactWidth(_collapsedWidth, TimeSpan.FromMilliseconds(400), _easeExpOut6, token);

        var fadeOut = MakeCompactNotificationExit(1, 0);
        fadeOut.Completed += (s, e) =>
        {
            if (token != _chargingGlanceToken) return;
            if (IsCompactSlotStale(token)) return;

            ChargingNotification.Visibility = Visibility.Collapsed;
            _isChargingNotificationVisible = false;
            _compactPillArbiter.Release(token);
            RestoreCompactMediaPresentation();
            _chargingGlanceToken = 0;

            if (_isMusicCompactMode && _currentMediaInfo != null)
            {
                MusicCompactContent.Visibility = Visibility.Visible;
                MusicCompactContent.Opacity = 0;
                var fadeInMusic = MakeCompactContentRestore();
                MusicCompactContent.BeginAnimation(OpacityProperty, fadeInMusic);
            }
            else
            {
                CollapsedContent.Visibility = Visibility.Visible;
                CollapsedContent.Opacity = 0;
                var fadeInCollapsed = MakeCompactContentRestore();
                CollapsedContent.BeginAnimation(OpacityProperty, fadeInCollapsed);
            }
        };

        ChargingNotification.BeginAnimation(OpacityProperty, fadeOut);

        var slideDown = MakeCompactNotificationExit(0, -4);
        ChargingNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
    }

    private void CancelChargingGlanceImmediate()
    {
        if (!_isChargingNotificationVisible) return;

        StopChargingNotificationDismissTimer();
        _isChargingNotificationVisible = false;
        _chargingGlanceToken = 0;

        ChargingNotification.BeginAnimation(OpacityProperty, null);
        ChargingNotificationTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ChargingNotification.Opacity = 0;
        ChargingNotification.Visibility = Visibility.Collapsed;
    }

    private void StopChargingNotificationDismissTimer()
    {
        var timer = _chargingNotificationDismissTimer;
        _chargingNotificationDismissTimer = null;
        timer?.Stop();
    }

    private void PlayChargingBounce()
    {
        if (_isExpanded || _isAnimating) return;

        int fps = VNotch.Services.AnimationConfig.TargetFps;

        double extra = ChargingWattText.Visibility == Visibility.Visible ? 56 : 28;
        double targetWidth = _collapsedWidth + extra;
        AnimateCompactWidth(targetWidth, TimeSpan.FromMilliseconds(500), _easeSoftSpring, _chargingGlanceToken);

        var durPeak = TimeSpan.FromMilliseconds(140);
        var durEnd = TimeSpan.FromMilliseconds(700);

        var bounceX = new DoubleAnimationUsingKeyFrames();
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.06, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceX, fps);

        var bounceY = new DoubleAnimationUsingKeyFrames();
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(durPeak), _easeQuadOut));
        bounceY.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(durEnd), _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounceY, fps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
    }

    private static void AnimateBrushTransition(FrameworkElement element, SolidColorBrush targetBrush)
    {
        SolidColorBrush? currentBrush = element switch
        {
            TextBlock tb => tb.Foreground as SolidColorBrush,
            Border border => border.Background as SolidColorBrush,
            _ => null
        };

        if (currentBrush == null || currentBrush.Color == targetBrush.Color)
        {
            if (element is TextBlock textBlock)
                textBlock.Foreground = targetBrush;
            else if (element is Border borderElement)
                borderElement.Background = targetBrush;
            return;
        }

        var colorAnimation = new ColorAnimation
        {
            To = targetBrush.Color,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };

        var animatedBrush = new SolidColorBrush(currentBrush.Color);
        if (element is TextBlock textBlockElement)
            textBlockElement.Foreground = animatedBrush;
        else if (element is Border borderElement2)
            borderElement2.Background = animatedBrush;

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(colorAnimation, VNotch.Services.AnimationConfig.TargetFps);
        animatedBrush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimation);
    }

    private void AnimateChargingBolt(bool show)
    {
        var opacityAnimation = new DoubleAnimation
        {
            To = show ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var scaleAnimation = new DoubleAnimation
        {
            To = show ? 1.0 : 0.95,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(opacityAnimation, VNotch.Services.AnimationConfig.TargetFps);
        ChargingBolt.BeginAnimation(OpacityProperty, opacityAnimation);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleAnimation, VNotch.Services.AnimationConfig.TargetFps);
        ChargingBoltScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        ChargingBoltScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
    }

    private void StartChargingPulse()
    {
        _chargingPulseWanted = true;
        if (!AmbientAnimationsAllowed) return;
        if (_chargingPulseStoryboard != null) return;

        _chargingPulseStoryboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(_chargingPulseStoryboard, VNotch.Services.AnimationConfig.TargetFps);

        var pulseAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 0.85,
            Duration = TimeSpan.FromMilliseconds(1000),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        Storyboard.SetTarget(pulseAnimation, BatteryFill);
        Storyboard.SetTargetProperty(pulseAnimation, new PropertyPath("Opacity", Array.Empty<object>()));
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(pulseAnimation, VNotch.Services.AnimationConfig.TargetFps);
        _chargingPulseStoryboard.Children.Add(pulseAnimation);

        _chargingPulseStoryboard.Begin();
    }

    private void StopChargingPulse()
    {
        _chargingPulseWanted = false;
        StopChargingPulseInternal();
    }

    private void StopChargingPulseInternal()
    {
        if (_chargingPulseStoryboard == null) return;

        _chargingPulseStoryboard.Stop();
        _chargingPulseStoryboard = null;

        var resetAnimation = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(resetAnimation, VNotch.Services.AnimationConfig.TargetFps);
        BatteryFill.BeginAnimation(OpacityProperty, resetAnimation);
    }

}
