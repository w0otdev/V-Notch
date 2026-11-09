using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private bool _appNotificationPointerDown;
    private bool _appNotificationDragging;
    private Point _appNotificationPointerOrigin;
    private Point _appNotificationLastPointer;
    private long _appNotificationLastPointerTime;
    private double _appNotificationPointerVelocity;

    internal void BeginAppNotificationInteraction(Point point, long timestamp)
    {
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting || _appNotificationPointerDown) return;
        _appNotificationPointerDown = true;
        _appNotificationDragging = false;
        _appNotificationPointerOrigin = _appNotificationLastPointer = point;
        _appNotificationLastPointerTime = timestamp;
        _appNotificationPointerVelocity = 0;
        PauseAppNotificationDismiss();
        AnimateAppNotificationHoverFeedback();
    }

    internal void UpdateAppNotificationInteraction(Point point, long timestamp)
    {
        if (!_appNotificationPointerDown) return;
        Vector delta = point - _appNotificationPointerOrigin;
        if (!_appNotificationDragging && Math.Abs(delta.X) < 5 && Math.Abs(delta.Y) < 5) return;
        if (!_appNotificationDragging)
        {
            _appNotificationDragging = true;
            SetAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, AppNotificationBannerTranslate.Y);
            AnimateAppNotificationHoverFeedback();
        }
        long elapsed = Math.Max(1, timestamp - _appNotificationLastPointerTime);
        _appNotificationPointerVelocity = (point.X - _appNotificationLastPointer.X) / elapsed;
        _appNotificationLastPointer = point;
        _appNotificationLastPointerTime = timestamp;
        double x = delta.X / (1 + Math.Abs(delta.X) / 280);
        double y = Math.Clamp(delta.Y * .12, -12, 12);
        SetAppNotificationValue(AppNotificationShellTranslate, TranslateTransform.XProperty, x);
        SetAppNotificationValue(AppNotificationShadowTranslate, TranslateTransform.XProperty, x);
        SetAppNotificationValue(AppNotificationShellTranslate, TranslateTransform.YProperty, y);
        SetAppNotificationValue(AppNotificationShadowTranslate, TranslateTransform.YProperty, y);
        SetAppNotificationOpacity(AppNotificationBanner, Math.Clamp(1 - Math.Abs(delta.X) / 360, .45, 1));
        _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
    }

    internal bool EndAppNotificationInteraction(Point point, long timestamp)
    {
        if (!_appNotificationPointerDown) return false;
        double delta = point.X - _appNotificationPointerOrigin.X;
        double velocity = timestamp - _appNotificationLastPointerTime <= 100 ? _appNotificationPointerVelocity : 0;
        bool dragging = _appNotificationDragging;
        bool dismiss = dragging && (Math.Abs(delta) >= 72 ||
            (Math.Abs(delta) >= 16 && Math.Abs(velocity) >= .65 && Math.Sign(delta) == Math.Sign(velocity)));
        ResetAppNotificationPointer();
        if (dismiss) BeginAppNotificationDismiss(Math.Sign(delta));
        else
        {
            SnapAppNotificationBack();
            if (!_isAppNotificationHovered && _settings.AutoDismissNotifications)
                ScheduleAppNotificationDismiss(_appNotificationRemainingMs, _appNotificationGeneration);
        }
        return !dragging;
    }

    private void SnapAppNotificationBack()
    {
        foreach (var translate in new[] { AppNotificationShellTranslate, AppNotificationShadowTranslate })
        {
            AnimateAppNotificationValue(translate, TranslateTransform.XProperty, 0, NotificationTransitionMs);
            AnimateAppNotificationValue(translate, TranslateTransform.YProperty, 0, NotificationTransitionMs);
        }
        AnimateAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, 0, NotificationTransitionMs);
        AnimateAppNotificationOpacity(AppNotificationBanner, 1, 180);
        AnimateAppNotificationHoverFeedback();
        BeginGlassHoverMotion(MakeAnim(0, TimeSpan.FromMilliseconds(NotificationTransitionMs), _easeSineInOut));
    }

    private void ResetAppNotificationPointer()
    {
        _appNotificationPointerDown = false;
        _appNotificationDragging = false;
        if (AppNotificationBanner.IsMouseCaptured) AppNotificationBanner.ReleaseMouseCapture();
    }

    private void AppNotificationBanner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
        e.Handled = true;
        AppNotificationBanner.Focus();
        BeginAppNotificationInteraction(e.GetPosition(this), Environment.TickCount64);
        if (!AppNotificationBanner.CaptureMouse())
        {
            CancelAppNotificationInteraction();
        }
    }

    private void AppNotificationBanner_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_appNotificationPointerDown) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelAppNotificationInteraction(); return; }
        e.Handled = true;
        UpdateAppNotificationInteraction(e.GetPosition(this), Environment.TickCount64);
    }

    private void AppNotificationBanner_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_appNotificationPointerDown) return;
        e.Handled = true;
        if (EndAppNotificationInteraction(e.GetPosition(this), Environment.TickCount64)) ActivateAppNotification();
    }

    private void AppNotificationBanner_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_appNotificationPointerDown) CancelAppNotificationInteraction();
    }

    private void CancelAppNotificationInteraction()
    {
        ResetAppNotificationPointer();
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
        SnapAppNotificationBack();
        if (!_isAppNotificationHovered && _settings.AutoDismissNotifications)
            ScheduleAppNotificationDismiss(_appNotificationRemainingMs, _appNotificationGeneration);
    }

    private void ActivateAppNotification(bool keyboard = false)
    {
        var active = _activeAppNotification;
        BeginAppNotificationDismiss(0, keyboard);
        if (active != null) _ = AppNotificationService.TryActivateApp(active.AppId, active.AppName);
    }

    private void AppNotificationBanner_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; BeginAppNotificationDismiss(0, immediate: true); }
        else if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            if (!e.IsRepeat) ActivateAppNotification(keyboard: true);
        }
    }
}
