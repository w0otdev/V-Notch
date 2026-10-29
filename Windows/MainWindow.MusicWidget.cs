using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using VNotch.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    #region Expanded Music Player Animations

    private bool _isMusicExpanded => _transitionCoordinator.ShapeState == VNotch.Controllers.NotchShapeState.MusicExpanded;

    private double GetMediaWidgetLayoutX()
    {
        try
        {
            return MediaWidgetContainer.TranslatePoint(new Point(0, 0), ExpandedContent).X;
        }
        catch
        {
            return 0;
        }
    }

    private void AnimateMediaWidgetLayoutFrom(
        double previousWidth,
        double previousX,
        Duration duration,
        IEasingFunction easing,
        Action onCompleted)
    {
        ExpandedContent.UpdateLayout();

        double targetWidth = MediaWidgetContainer.ActualWidth;
        double targetX = GetMediaWidgetLayoutX();
        double initialScaleX = targetWidth > 0
            ? Math.Clamp(previousWidth / targetWidth, 0.05, 8.0)
            : 1.0;
        double initialTranslateX = previousX - targetX;

        MediaWidgetLayoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MediaWidgetLayoutTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        MediaWidgetLayoutScale.ScaleX = initialScaleX;
        MediaWidgetLayoutTranslate.X = initialTranslateX;
        // Album art and progress remain live during the transform; caching this
        // container can replay the pre-transition image for a frame.

        var scaleAnim = new DoubleAnimation(initialScaleX, 1.0, duration)
        {
            EasingFunction = easing
        };
        var translateAnim = new DoubleAnimation(initialTranslateX, 0.0, duration)
        {
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(scaleAnim, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(translateAnim, AnimationConfig.TargetFps);

        scaleAnim.Completed += (s, e) =>
        {
            MediaWidgetLayoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            MediaWidgetLayoutTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            MediaWidgetLayoutScale.ScaleX = 1.0;
            MediaWidgetLayoutTranslate.X = 0.0;
            onCompleted();
        };

        MediaWidgetLayoutScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        MediaWidgetLayoutTranslate.BeginAnimation(TranslateTransform.XProperty, translateAnim);
    }

    private void CollapseMusicWidget(long? transitionId = null)
    {
        if (!transitionId.HasValue)
        {
            if (_isDebugViewLocked) return;
            _transitionCoordinator.RequestView(VNotch.Models.NotchView.Media, "CollapseMusicWidget");
            return;
        }
        long generation = transitionId.Value;
        UpdateProgressSectionLayout();

        ResetCalendarHoverFocusVisualState();

        var collapseDuration = new Duration(TimeSpan.FromMilliseconds(400));
        var contentDelay = TimeSpan.FromMilliseconds(80);

        var fadeOutInline = MakeAnim(1d, 0d, _dur150, _easePowerIn2, null);
        fadeOutInline.Completed += (s, e) =>
        {
            InlineControls.Visibility = Visibility.Collapsed;
        };
        InlineControls.BeginAnimation(OpacityProperty, fadeOutInline);

        double currentWidth = MediaWidgetContainer.ActualWidth;
        double currentX = GetMediaWidgetLayoutX();

        MediaWidgetContainer.BeginAnimation(WidthProperty, null);
        MediaWidgetContainer.BeginAnimation(MarginProperty, null);
        MediaWidgetContainer.Width = double.NaN;
        MediaWidgetContainer.Margin = new Thickness(-8, 0, 0, 0);
        MediaWidgetContainer.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumnSpan(MediaWidgetContainer, 1);
        Panel.SetZIndex(MediaWidgetContainer, 0);

        AnimateMediaWidgetLayoutFrom(currentWidth, currentX, collapseDuration, _easeExpOut7, () =>
        {
            if (generation != _transitionCoordinator.ActiveTransitionId) return;
            UpdateProgressSectionLayout();
            _transitionCoordinator.CompleteTransition(generation);
            UpdateProgressTimerState();
        });

        MediaPlayback.MediaControls.BeginAnimation(OpacityProperty, null);
        MediaPlayback.MediaControls.Opacity = 0;
        MediaPlayback.MediaControls.Visibility = Visibility.Visible;
        var fadeInControls = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, contentDelay);
        MediaPlayback.MediaControls.BeginAnimation(OpacityProperty, fadeInControls);

        if (_isLyricsActive)
        {
            CalendarWidget.Visibility = Visibility.Collapsed;
            GreetingSection.Visibility = Visibility.Collapsed;
            LyricsWidget.BeginAnimation(OpacityProperty, null);
            LyricsWidget.Visibility = Visibility.Visible;
            LyricsWidget.Opacity = 0;
            var fadeInLyrics = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
            LyricsWidget.BeginAnimation(OpacityProperty, fadeInLyrics);
        }
        else
        {
            CalendarWidget.BeginAnimation(OpacityProperty, null);
            CalendarWidget.Opacity = 0;
            CalendarWidget.Visibility = IsNoWidgetMode ? Visibility.Collapsed : Visibility.Visible;
            var fadeInCalendar = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
            CalendarWidget.BeginAnimation(OpacityProperty, fadeInCalendar);
        }

        if (_settings.ShowBatteryIndicator)
        {
            BatterySection.BeginAnimation(OpacityProperty, null);
            BatterySection.Opacity = 0;
            BatterySection.Visibility = Visibility.Visible;
            var fadeInBattery = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(100));
            BatterySection.BeginAnimation(OpacityProperty, fadeInBattery);
        }
        else
        {
            BatterySection.Visibility = Visibility.Collapsed;
        }

        NavIconsPanel.BeginAnimation(OpacityProperty, null);
        NavIconsPanel.Opacity = 0;
        NavIconsPanel.Visibility = Visibility.Visible;
        var fadeInNavPanel = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(100));
        NavIconsPanel.BeginAnimation(OpacityProperty, fadeInNavPanel);

        if (_isSecondaryView)
        {
            NavIconsBackground.BeginAnimation(OpacityProperty, null);
            NavIconsBackground.Opacity = 0;
            NavIconsBackground.Visibility = Visibility.Visible;
            var fadeInNavBg = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(100));
            NavIconsBackground.BeginAnimation(OpacityProperty, fadeInNavBg);
        }

        if (_isUpdateAvailable && UpdateNotificationButton != null)
        {
            UpdateNotificationButton.BeginAnimation(OpacityProperty, null);
            UpdateNotificationButton.Opacity = 0;
            UpdateNotificationButton.Visibility = Visibility.Visible;
            UpdateNotificationButton.IsHitTestVisible = true;
            UpdateNotificationButton.Cursor = System.Windows.Input.Cursors.Hand;

            if (UpdateIconBrush != null)
            {
                UpdateIconBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                UpdateIconBrush.Color = Color.FromRgb(48, 209, 88);
            }

            StartUpdatePulseAnimation();

            var fadeInUpdate = MakeAnim(0d, 1.0d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(110));
            UpdateNotificationButton.BeginAnimation(OpacityProperty, fadeInUpdate);
        }

        SettingsButton.BeginAnimation(OpacityProperty, null);
        SettingsButton.Opacity = 0;
        SettingsButton.Visibility = Visibility.Visible;
        var fadeInSettings = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(120));
        SettingsButton.BeginAnimation(OpacityProperty, fadeInSettings);

        GreetingSection.BeginAnimation(OpacityProperty, null);
        GreetingSection.Opacity = 0;
        bool showGreeting = ShouldShowGreetingSection;
        GreetingSection.Visibility = showGreeting ? Visibility.Visible : Visibility.Collapsed;
        if (showGreeting)
        {
            var fadeInGreeting = MakeAnim(0d, 1d, new Duration(TimeSpan.FromMilliseconds(300)), _easePowerOut3, TimeSpan.FromMilliseconds(140));
            GreetingSection.BeginAnimation(OpacityProperty, fadeInGreeting);
        }
    }

    private void UpdateProgressSectionLayout()
    {
        MediaPlayback.IsMusicExpanded = _isMusicExpanded;
        MediaPlayback.EnableBlurEffects = _settings.EnableBlurEffects;
        MediaPlayback.UpdateProgressSectionLayout();
    }

    private void MediaWidgetContainer_SizeChanged(object sender, SizeChangedEventArgs e) => MediaPlayback.RefreshMediaMarquee();


    #endregion
}
