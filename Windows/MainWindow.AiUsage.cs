using System.Windows;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch;

public partial class MainWindow
{
    private bool _aiUsageLayoutQueued;
    private readonly List<AiQuotaAlert> _pendingAiQuotaAlerts = [];
    private UiScheduledTimer? _aiQuotaNotificationTimer;
    private AiQuotaAlert? _aiQuotaPreview;

    internal void PreviewAiQuotaNotification(int threshold, string provider)
    {
        if (_cleanedUp) return;
        _aiQuotaPreview = new(provider,
            new AiQuotaWindow("aiUsage.primary", 101 - threshold, 300, DateTimeOffset.UtcNow.AddHours(1)), threshold);
        EnsureAiQuotaNotificationTimer();
        TryPresentAiQuotaNotification();
    }

    private void EnsureAiQuotaNotificationTimer() =>
        _aiQuotaNotificationTimer ??= new UiScheduledTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => TryPresentAiQuotaNotification(), Dispatcher);

    private void OnAiQuotaLow(IReadOnlyList<AiQuotaAlert> alerts)
    {
        if (_cleanedUp) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_cleanedUp || !_settings.EnableAiUsageMonitoring) return;
            _pendingAiQuotaAlerts.AddRange(alerts);
            EnsureAiQuotaNotificationTimer();
            TryPresentAiQuotaNotification();
        });
    }

    private void TryPresentAiQuotaNotification()
    {
        if (_cleanedUp || (!_settings.EnableAiUsageMonitoring && _aiQuotaPreview == null))
        {
            DisposeAiQuotaNotifications();
            return;
        }
        // Wait for the notch to be free instead of interrupting an open view or losing the alert.
        if (!IsLoaded || !_isNotchVisible || _isGreetingActive || _isExpanded || _isAnimating ||
            IsCountdownCompletionVisualActive || _isAppNotificationBannerVisible || _isAppNotificationBannerExiting) return;

        var visibleAlerts = new List<AiQuotaAlert>();
        if (_aiQuotaPreview is { } preview) visibleAlerts.Add(preview);
        foreach (var alert in _pendingAiQuotaAlerts)
        {
            if (!_settings.EnableAiUsageMonitoring || alert.Window.IsExpired(DateTimeOffset.UtcNow)) continue;
            if (alert.Provider == "Codex" && (!_settings.TrackCodexUsage || _settings.EnableLocalOnlyMode || !_settings.AllowOnlineAi)) continue;
            if (alert.Provider == "Claude" && !_settings.TrackClaudeUsage) continue;
            visibleAlerts.Add(alert);
        }
        DisposeAiQuotaNotifications();
        if (visibleAlerts.Count == 0) return;
        PresentAppNotificationBanner(new AppNotificationInfo
        {
            AppName = "V-Notch",
            AppIcon = Icon,
        }, isQuotaAlert: true, quotaAlerts: visibleAlerts);
    }

    private void DisposeAiQuotaNotifications()
    {
        _aiQuotaNotificationTimer?.Dispose();
        _aiQuotaNotificationTimer = null;
        _pendingAiQuotaAlerts.Clear();
        _aiQuotaPreview = null;
    }

    private double MeasureAiUsageViewHeight()
    {
        AiUsageViewControl.PrepareForDisplay();
        double contentWidth = Math.Max(0, AiUsageViewWidth - AiUsageContent.Margin.Left - AiUsageContent.Margin.Right);
        AiUsageViewControl.Measure(new Size(contentWidth, double.PositiveInfinity));
        return Math.Ceiling(Math.Max(_expandedHeight,
            AiUsageViewControl.DesiredSize.Height + AiUsageContent.Margin.Top + AiUsageContent.Margin.Bottom));
    }

    private void FitAiUsageView()
    {
        double height = MeasureAiUsageViewHeight();
        AiUsageContent.Width = AiUsageViewWidth - AiUsageContent.Margin.Left - AiUsageContent.Margin.Right;
        AiUsageContent.Height = Math.Max(0, height - AiUsageContent.Margin.Top - AiUsageContent.Margin.Bottom);
        ResizeHostWindowHeight(height);
        NotchBorder.BeginAnimation(HeightProperty, null);
        NotchBorder.Height = height;
    }

    private void ScheduleAiUsageLayout()
    {
        if (_aiUsageLayoutQueued || _cleanedUp) return;
        _aiUsageLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _aiUsageLayoutQueued = false;
            if (!_cleanedUp && _isExpanded && _isAiUsageView && !_isAnimating) FitAiUsageView();
        });
    }
}
