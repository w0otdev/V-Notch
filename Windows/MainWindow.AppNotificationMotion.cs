using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private const int NotificationFeedbackMs = 90;
    private const int NotificationTransitionMs = 240;
    private const int NotificationOpenMs = 360;

    private static DoubleAnimationUsingKeyFrames AppNotificationMotion(double from, double to, int milliseconds,
        int delay = 0, bool linear = false, IEasingFunction? easing = null)
    {
        var motion = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0) motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        var end = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + Math.Max(1, milliseconds)));
        if (linear) motion.KeyFrames.Add(new LinearDoubleKeyFrame(to, end));
        else motion.KeyFrames.Add(new EasingDoubleKeyFrame(to, end, easing ?? _easeAppleOut));
        Timeline.SetDesiredFrameRate(motion, AnimationConfig.TargetFps);
        return motion;
    }

    private static void SetAppNotificationValue(Animatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }

    private static void AnimateAppNotificationValue(Animatable target, DependencyProperty property, double to,
        int milliseconds, int delay = 0, bool linear = false, IEasingFunction? easing = null)
    {
        double from = (double)target.GetValue(property);
        if (AnimationConfig.ReduceMotion || milliseconds <= 0)
        {
            SetAppNotificationValue(target, property, to);
            return;
        }
        SetAppNotificationValue(target, property, from);
        target.BeginAnimation(property, AppNotificationMotion(from, to, milliseconds, delay, linear, easing));
    }

    private static void SetAppNotificationOpacity(UIElement target, double value)
    {
        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = value;
    }

    private static void AnimateAppNotificationOpacity(UIElement target, double to, int milliseconds, int delay = 0, IEasingFunction? easing = null)
    {
        double from = target.Opacity;
        if (milliseconds <= 0)
        {
            SetAppNotificationOpacity(target, to);
            return;
        }
        SetAppNotificationOpacity(target, from);
        if (AnimationConfig.ReduceMotion) { milliseconds = Math.Min(milliseconds, 90); delay = 0; }
        target.BeginAnimation(OpacityProperty, AppNotificationMotion(from, to, milliseconds, delay, easing: easing));
    }

    private void PresentAppNotification(bool replacing)
    {
        SetAppNotificationOpacity(AppNotificationBanner, replacing ? .5 : 0);
        SetAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty,
            replacing || AnimationConfig.ReduceMotion ? 0 : 6);
        if (_isAiQuotaNotification && !AnimationConfig.ReduceMotion)
        {
            SetAppNotificationValue(AppNotificationBannerScale, ScaleTransform.ScaleXProperty, .98);
            SetAppNotificationValue(AppNotificationBannerScale, ScaleTransform.ScaleYProperty, .96);
            AnimateAppNotificationValue(AppNotificationBannerScale, ScaleTransform.ScaleXProperty, 1, 360, easing: _easeSoftSpring);
            AnimateAppNotificationValue(AppNotificationBannerScale, ScaleTransform.ScaleYProperty, 1, 360, easing: _easeSoftSpring);
        }
        if (!_isAiQuotaNotification) TransitionAppNotificationBlurCover(entering: true);
        AnimateAppNotificationOpacity(AppNotificationBanner, 1, NotificationTransitionMs, easing: _easeAppleOut);
        AnimateAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, 0,
            NotificationTransitionMs, easing: _easeAppleOut);
        AppNotificationHoverSurface.Visibility = Visibility.Visible;
        AnimateAppNotificationHoverFeedback();
    }

    private void FreezeAppNotificationShell()
    {
        double width = NotchBorder.Width, height = NotchBorder.Height;
        NotchBorder.BeginAnimation(WidthProperty, null);
        NotchBorder.BeginAnimation(HeightProperty, null);
        if (double.IsFinite(width)) NotchBorder.Width = width;
        if (double.IsFinite(height)) NotchBorder.Height = height;
    }

    private void AnimateAppNotificationShell(double width, double height, double radius, int milliseconds,
        Action? completed = null, IEasingFunction? easing = null)
    {
        int morph = ++_appNotificationMorphGeneration;
        double fromWidth = double.IsFinite(NotchBorder.Width) ? NotchBorder.Width : GetCollapsedWidth();
        double fromHeight = double.IsFinite(NotchBorder.Height) ? NotchBorder.Height : GetCollapsedHeight();
        FreezeAppNotificationShell();
        SetAppNotificationValue(NotchScale, ScaleTransform.ScaleXProperty, 1);
        SetAppNotificationValue(NotchShadowScale, ScaleTransform.ScaleXProperty, 1);
        if (AnimationConfig.ReduceMotion || milliseconds == 0)
        {
            NotchBorder.Width = width;
            NotchBorder.Height = height;
            BeginAnimation(CurrentCornerRadiusProperty, null);
            CurrentCornerRadius = radius;
            completed?.Invoke();
            return;
        }
        var widthMotion = MakeAnim(fromWidth, width, TimeSpan.FromMilliseconds(milliseconds), easing ?? _easeAppleOut);
        var heightMotion = MakeAnim(fromHeight, height, TimeSpan.FromMilliseconds(milliseconds), easing ?? _easeAppleOut);
        heightMotion.Completed += (_, _) =>
        {
            if (morph != _appNotificationMorphGeneration) return;
            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            NotchBorder.Width = width;
            NotchBorder.Height = height;
            completed?.Invoke();
        };
        NotchBorder.BeginAnimation(WidthProperty, widthMotion);
        NotchBorder.BeginAnimation(HeightProperty, heightMotion);
        AnimateCornerRadius(radius, TimeSpan.FromMilliseconds(milliseconds));
        BeginGlassHoverMotion(heightMotion);
    }

    private void BeginAppNotificationDismiss(int direction, bool immediate = false)
    {
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
        StopAppNotificationDismissTimer();
        ResetAppNotificationPointer();
        _isAppNotificationBannerExiting = true;
        _isAppNotificationHovered = false;
        int generation = ++_appNotificationGeneration;
        AppNotificationBanner.IsHitTestVisible = false;
        if (_isAiQuotaNotification) AiQuotaBannerContent.PlayExit(immediate);
        if (!immediate && !_isAiQuotaNotification) TransitionAppNotificationBlurCover(entering: false);
        AnimateAppNotificationOpacity(AppNotificationBanner, 0, immediate ? 0 : _isAiQuotaNotification ? 160 : 180,
            easing: _isAiQuotaNotification ? _easeAppleOut : _easeQuadIn);
        AnimateAppNotificationHoverFeedback();
        AnimateAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, direction == 0 ? -3 : 0,
            180, easing: _easeQuadIn);
        if (direction != 0)
        {
            double travel = Math.Abs(AppNotificationShellTranslate.X) + 80;
            AnimateAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.XProperty,
                direction * travel, 180, easing: _easeQuadIn);
        }
        foreach (var translate in new[] { AppNotificationShellTranslate, AppNotificationShadowTranslate })
        {
            AnimateAppNotificationValue(translate, TranslateTransform.XProperty, 0, NotificationTransitionMs);
            AnimateAppNotificationValue(translate, TranslateTransform.YProperty, 0, NotificationTransitionMs);
        }
        AnimateAppNotificationShell(_collapsedWidth > 0 ? _collapsedWidth : GetCollapsedWidth(),
            _collapsedHeight > 0 ? _collapsedHeight : GetCollapsedHeight(), _cornerRadiusCollapsed,
            immediate ? 0 : NotificationTransitionMs, () =>
            {
                if (generation != _appNotificationGeneration) return;
                ResetAppNotificationVisuals();
                AppNotificationBanner.Opacity = 0;
                AppNotificationBanner.Visibility = Visibility.Collapsed;
                _isAppNotificationBannerVisible = false;
                _isAppNotificationBannerExiting = false;
                _activeAppNotification = null;
                RestorePrivacyDotVisibility();
                if (_isExpanded || _isAnimating) return;
                if (_isMusicCompactMode && _currentMediaInfo != null)
                {
                    MusicCompactContent.Visibility = Visibility.Visible;
                    MusicCompactContent.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
                    RestoreCompactMediaPresentation();
                }
                else
                {
                    CollapsedContent.Visibility = Visibility.Visible;
                    CollapsedContent.BeginAnimation(OpacityProperty, MakeCompactContentRestore());
                }
            }, easing: _easeAppleInOut);
    }

    private void ResetAppNotificationVisuals()
    {
        ResetAppNotificationBlurCover();
        foreach (var element in new UIElement[] { AppNotificationBanner, AppNotificationIconHost,
                     AppNotificationAppNameText, AppNotificationTitleText, AppNotificationBodyText })
            SetAppNotificationOpacity(element, 1);
        SetAppNotificationOpacity(HoverGlow, 0);
        SetAppNotificationOpacity(AppNotificationHoverSurface, 0);
        AppNotificationHoverSurface.Visibility = Visibility.Collapsed;
        foreach (var translate in new[] { AppNotificationShellTranslate, AppNotificationShadowTranslate,
                     AppNotificationBannerTranslate, AppNotificationPressTranslate, AppNotificationIconTranslate, AppNotificationAppNameTranslate,
                     AppNotificationTitleTranslate, AppNotificationBodyTranslate })
        {
            SetAppNotificationValue(translate, TranslateTransform.XProperty, 0);
            SetAppNotificationValue(translate, TranslateTransform.YProperty, 0);
        }
        foreach (var scale in new[] { AppNotificationBannerScale, AppNotificationIconScale })
        {
            SetAppNotificationValue(scale, ScaleTransform.ScaleXProperty, 1);
            SetAppNotificationValue(scale, ScaleTransform.ScaleYProperty, 1);
        }
        SetAppNotificationValue(AppNotificationIconRotate, RotateTransform.AngleProperty, 0);
        SetAppNotificationValue(AppNotificationProgressScale, ScaleTransform.ScaleXProperty, 1);
    }
}
