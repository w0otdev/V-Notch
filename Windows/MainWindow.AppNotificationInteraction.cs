using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnAppNotifyDragState
    {
        public double LastX;
        public double LastY;
        public long LastTimestamp;
        public double Velocity;
        public byte IsDragging;
        public byte Reserved0;
        public byte Reserved1;
        public byte Reserved2;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_step_drag_interaction(
        ref VnAppNotifyDragState ioState,
        double pointX,
        double pointY,
        double originX,
        double originY,
        long timestamp,
        out double outTranslateX,
        out double outTranslateY,
        out double outOpacity,
        out byte outMoved,
        out byte outStartedDragging);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_appnotify_eval_drag_release(
        double pointX,
        double originX,
        long lastTimestamp,
        long timestamp,
        double velocity,
        byte isDragging,
        out byte outDismiss,
        out int outDismissDirection,
        out byte outIsClick);

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

        var dragState = new VnAppNotifyDragState
        {
            LastX = _appNotificationLastPointer.X,
            LastY = _appNotificationLastPointer.Y,
            LastTimestamp = _appNotificationLastPointerTime,
            Velocity = _appNotificationPointerVelocity,
            IsDragging = _appNotificationDragging ? (byte)1 : (byte)0
        };

        if (vn_appnotify_step_drag_interaction(
                ref dragState,
                point.X,
                point.Y,
                _appNotificationPointerOrigin.X,
                _appNotificationPointerOrigin.Y,
                timestamp,
                out double x,
                out double y,
                out double opacity,
                out byte moved,
                out byte startedDragging) != 0 ||
            moved == 0)
        {
            return;
        }

        if (startedDragging != 0)
        {
            _appNotificationDragging = true;
            SetAppNotificationValue(AppNotificationBannerTranslate, TranslateTransform.YProperty, AppNotificationBannerTranslate.Y);
            AnimateAppNotificationHoverFeedback();
        }
        else
        {
            _appNotificationDragging = dragState.IsDragging != 0;
        }

        _appNotificationPointerVelocity = dragState.Velocity;
        _appNotificationLastPointer = new Point(dragState.LastX, dragState.LastY);
        _appNotificationLastPointerTime = dragState.LastTimestamp;
        SetAppNotificationValue(AppNotificationShellTranslate, TranslateTransform.XProperty, x);
        SetAppNotificationValue(AppNotificationShadowTranslate, TranslateTransform.XProperty, x);
        SetAppNotificationValue(AppNotificationShellTranslate, TranslateTransform.YProperty, y);
        SetAppNotificationValue(AppNotificationShadowTranslate, TranslateTransform.YProperty, y);
        SetAppNotificationOpacity(AppNotificationBanner, opacity);
        _liquidGlass?.SetLiveRegion(GetGlassCaptureRegion());
    }

    internal bool EndAppNotificationInteraction(Point point, long timestamp)
    {
        if (!_appNotificationPointerDown) return false;

        _ = vn_appnotify_eval_drag_release(
            point.X,
            _appNotificationPointerOrigin.X,
            _appNotificationLastPointerTime,
            timestamp,
            _appNotificationPointerVelocity,
            _appNotificationDragging ? (byte)1 : (byte)0,
            out byte dismissByte,
            out int dismissDirection,
            out byte isClickByte);

        ResetAppNotificationPointer();
        if (dismissByte != 0) BeginAppNotificationDismiss(dismissDirection);
        else
        {
            SnapAppNotificationBack();
            if (!_isAppNotificationHovered && _settings.AutoDismissNotifications)
                ScheduleAppNotificationDismiss(_appNotificationRemainingMs, _appNotificationGeneration);
        }
        return isClickByte != 0;
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
