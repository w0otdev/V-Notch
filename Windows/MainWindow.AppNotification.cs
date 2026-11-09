using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private AppNotificationService? _appNotificationService;
    private bool _isAppNotificationBannerVisible;
    private bool _isAppNotificationBannerExiting;
    private bool _isAppNotificationHovered;
    private int _appNotificationGeneration;
    private int _appNotificationMorphGeneration;
    private double _appNotificationBaseWidth;
    private double _appNotificationBaseHeight;
    private double _appNotificationBaseCornerRadius;
    private AppNotificationInfo? _activeAppNotification;
    private UiScheduledTimer? _appNotificationDismissTimer;
    private long _appNotificationDismissDeadline;
    private int _appNotificationRemainingMs;

    internal bool IsAppNotificationBannerActive => _isAppNotificationBannerVisible;

    private void InitializeAppNotificationBanner()
    {
        _appNotificationService = new AppNotificationService(() => !_cleanedUp && _settings.ShowSystemNotifications);
        _appNotificationService.NotificationReceived += OnAppNotificationReceived;
        _appNotificationService.Start();
    }

    private void DisposeAppNotificationBanner()
    {
        CancelAppNotificationBannerImmediate();
        var service = _appNotificationService;
        _appNotificationService = null;
        if (service != null)
        {
            service.NotificationReceived -= OnAppNotificationReceived;
            service.Dispose();
        }
    }

    private void OnAppNotificationReceived(AppNotificationInfo notification)
    {
        if (_cleanedUp) return;
        Dispatcher.BeginInvoke(() => ShowAppNotification(notification));
    }

    internal void ShowAppNotification(AppNotificationInfo? notification)
    {
        if (notification == null || _cleanedUp || !_settings.ShowSystemNotifications) return;
        if (!_isNotchVisible || _isGreetingActive || _isExpanded || _isAnimating || IsCountdownCompletionVisualActive) return;

        WakeFromIdle();
        int generation = ++_appNotificationGeneration;
        bool replacing = _isAppNotificationBannerVisible && !_isAppNotificationBannerExiting;
        _isAppNotificationBannerVisible = true;
        _isAppNotificationBannerExiting = false;
        _activeAppNotification = notification;
        StopAppNotificationDismissTimer();
        ResetAppNotificationPointer();
        ResetAppNotificationVisuals();

        if (_compactPillArbiter.ActiveSlot != CompactPillSlot.None)
        {
            CancelCompactSlotImmediate(_compactPillArbiter.ActiveSlot);
            _compactPillArbiter.ForceClear();
        }
        if (_isCompactThumbnailHovered)
        {
            _isCompactThumbnailHovered = false;
            _compactThumbnailHoverLeaveTimer.Stop();
        }
        CompactHoverInfo.BeginAnimation(OpacityProperty, null);
        CompactHoverInfo.Opacity = 0;
        HidePrivacyIndicators();
        CollapsedContent.BeginAnimation(OpacityProperty, null);
        CollapsedContent.Opacity = 0;
        CollapsedContent.Visibility = Visibility.Collapsed;
        MusicCompactContent.BeginAnimation(OpacityProperty, null);
        MusicCompactContent.Opacity = 0;
        MusicCompactContent.Visibility = Visibility.Collapsed;

        ApplyAppNotificationContent(notification);
        var metrics = AppNotificationService.ComputeBannerMetrics(
            _collapsedWidth > 0 ? _collapsedWidth : GetCollapsedWidth(),
            _collapsedHeight > 0 ? _collapsedHeight : GetCollapsedHeight(),
            _expandedWidth, _cornerRadiusCollapsed, _cornerRadiusExpanded,
            _settings.EnableDynamicIslandMode, AnimationConfig.ReduceMotion,
            _settings.NotificationDuration, AppNotificationTitleText.Text, AppNotificationBodyText.Text);
        _appNotificationBaseWidth = metrics.TargetWidth;
        _appNotificationBaseCornerRadius = metrics.TargetCornerRadius;
        AppNotificationBanner.Width = Math.Max(220, metrics.TargetWidth - 36);
        AppNotificationBanner.Visibility = Visibility.Visible;
        AppNotificationProgressTrack.Visibility = _settings.AutoDismissNotifications ? Visibility.Visible : Visibility.Collapsed;
        AppNotificationBanner.Measure(new Size(metrics.TargetWidth, double.PositiveInfinity));
        _appNotificationBaseHeight = Math.Max(metrics.TargetHeight, AppNotificationBanner.DesiredSize.Height + 4);
        if (Height < _appNotificationBaseHeight + 32) ResizeHostWindowHeight(_appNotificationBaseHeight);

        AppNotificationBanner.IsHitTestVisible = true;
        PresentAppNotification(replacing);
        AnimateAppNotificationShell(
            _appNotificationBaseWidth, _appNotificationBaseHeight,
            _appNotificationBaseCornerRadius, NotificationOpenMs);
        _appNotificationRemainingMs = metrics.DismissDurationMs;
        if (!_isAppNotificationHovered && _settings.AutoDismissNotifications)
            ScheduleAppNotificationDismiss(_appNotificationRemainingMs, generation);
    }

    private void ApplyAppNotificationContent(AppNotificationInfo notification)
    {
        string appName = string.IsNullOrWhiteSpace(notification.AppName) ? "Notification" : notification.AppName;
        string rawTitle = notification.Title ?? string.Empty;
        string rawBody = notification.Body ?? string.Empty;

        string displayTitle;
        string displayBody;
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            displayTitle = !string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : appName;
            displayBody = rawBody;
        }
        else if (!string.IsNullOrWhiteSpace(rawTitle) &&
                 !string.Equals(rawTitle, appName, StringComparison.OrdinalIgnoreCase))
        {
            displayTitle = appName;
            displayBody = rawTitle;
        }
        else
        {
            displayTitle = !string.IsNullOrWhiteSpace(rawTitle) ? rawTitle : appName;
            displayBody = string.Empty;
        }

        AppNotificationAppNameText.Text = appName;
        AppNotificationTitleText.Text = displayTitle;
        AppNotificationBodyText.Text = displayBody;
        AppNotificationBodyText.Visibility = string.IsNullOrWhiteSpace(displayBody) ? Visibility.Collapsed : Visibility.Visible;
        AppNotificationTimeText.Text = "now";
        AppNotificationAvatarInitialsText.Text = AppNotificationService.ExtractInitials(displayTitle, appName);

        ImageSource? icon = AppNotificationService.NormalizeAppIcon(notification.AppIcon)
            ?? AppNotificationService.TryResolveAppIcon(notification.AppId, notification.AppName);
        AppNotificationIconImage.Source = icon;
        AppNotificationIconImage.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        AppNotificationDefaultIcon.Visibility = icon != null ? Visibility.Collapsed : Visibility.Visible;

        double collapsedH = _collapsedHeight > 0 ? _collapsedHeight : GetCollapsedHeight();
        double topOffset = _settings.EnableDynamicIslandMode
            ? Math.Max(18.0, collapsedH * 0.55)
            : Math.Max(26.0, collapsedH - 6.0);
        AppNotificationBanner.Margin = new Thickness(0, topOffset, 0, 14);
    }

    internal void AnimateAppNotificationHover(bool isHovered)
    {
        if (!_isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;
        _isAppNotificationHovered = isHovered;
        if (isHovered) PauseAppNotificationDismiss();
        else if (!_appNotificationPointerDown && _settings.AutoDismissNotifications)
            ScheduleAppNotificationDismiss(_appNotificationRemainingMs, _appNotificationGeneration);

        AnimateAppNotificationHoverFeedback();
    }

    private void AnimateAppNotificationHoverFeedback()
    {
        bool interactive = !_isAppNotificationBannerExiting;
        bool hovered = interactive && _isAppNotificationHovered;
        bool pressed = interactive && _appNotificationPointerDown && !_appNotificationDragging;
        bool dragging = interactive && _appNotificationDragging;
        int duration = pressed || hovered ? NotificationFeedbackMs : 180;
        AnimateAppNotificationOpacity(HoverGlow, 0, duration);
        AnimateAppNotificationOpacity(AppNotificationHoverSurface,
            pressed ? .065 : hovered && !dragging ? .035 : 0, duration);
        AnimateAppNotificationValue(AppNotificationPressTranslate, TranslateTransform.YProperty,
            pressed && !AnimationConfig.ReduceMotion ? 1 : 0, duration, easing: _easeAppleOut);
    }

    internal void DismissAppNotificationBanner() => BeginAppNotificationDismiss(0);

    internal void CancelAppNotificationBannerImmediate()
    {
        if (!_isAppNotificationBannerVisible && !_isAppNotificationBannerExiting) return;
        ++_appNotificationGeneration;
        ++_appNotificationMorphGeneration;
        StopAppNotificationDismissTimer();
        ResetAppNotificationPointer();
        FreezeAppNotificationShell();
        ResetAppNotificationVisuals();
        _isAppNotificationBannerVisible = false;
        _isAppNotificationBannerExiting = false;
        _isAppNotificationHovered = false;
        _activeAppNotification = null;
        AppNotificationBanner.Opacity = 0;
        AppNotificationBanner.Visibility = Visibility.Collapsed;
        AppNotificationBanner.IsHitTestVisible = false;
    }

    private void ScheduleAppNotificationDismiss(int durationMs, int generation)
    {
        if (!_settings.AutoDismissNotifications || _isAppNotificationHovered || _appNotificationPointerDown ||
            !_isAppNotificationBannerVisible || _isAppNotificationBannerExiting || _appNotificationDismissTimer != null) return;
        int remaining = Math.Clamp(durationMs, 1, 15000);
        _appNotificationRemainingMs = remaining;
        _appNotificationDismissDeadline = Environment.TickCount64 + remaining;
        var timer = new UiScheduledTimer { Interval = TimeSpan.FromMilliseconds(remaining) };
        _appNotificationDismissTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (!ReferenceEquals(_appNotificationDismissTimer, timer) || generation != _appNotificationGeneration) return;
            _appNotificationDismissTimer = null;
            _appNotificationDismissDeadline = 0;
            DismissAppNotificationBanner();
        };
        if (!AnimationConfig.ReduceMotion)
            AnimateAppNotificationValue(AppNotificationProgressScale, ScaleTransform.ScaleXProperty, 0, remaining, linear: true);
        timer.Start();
    }

    private void PauseAppNotificationDismiss()
    {
        if (_appNotificationDismissDeadline > 0)
            _appNotificationRemainingMs = (int)Math.Clamp(_appNotificationDismissDeadline - Environment.TickCount64, 1, 15000);
        double progress = AppNotificationProgressScale.ScaleX;
        StopAppNotificationDismissTimer();
        SetAppNotificationValue(AppNotificationProgressScale, ScaleTransform.ScaleXProperty, progress);
    }

    private void StopAppNotificationDismissTimer()
    {
        var timer = _appNotificationDismissTimer;
        _appNotificationDismissTimer = null;
        _appNotificationDismissDeadline = 0;
        timer?.Stop();
        timer?.Dispose();
    }

    private void AppNotificationBanner_MouseEnter(object sender, MouseEventArgs e) => AnimateAppNotificationHover(true);
    private void AppNotificationBanner_MouseLeave(object sender, MouseEventArgs e) => AnimateAppNotificationHover(false);

    private void AppNotificationBanner_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DismissAppNotificationBanner();
    }
}
