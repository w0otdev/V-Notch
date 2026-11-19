using System.Windows;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    internal SettingsPreparationCoordinator SettingsPreparation { get; }
    private bool _firstOpenUiPrepared;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new MainWindowAutomationPeer(this);

    private sealed class MainWindowAutomationPeer(MainWindow owner)
        : System.Windows.Automation.Peers.WindowAutomationPeer(owner)
    {
        protected override System.Collections.Generic.List<System.Windows.Automation.Peers.AutomationPeer>? GetChildrenCore() => null;
    }

    private SettingsWindow CreateSettingsWindow(bool prepareForFirstShow = false) => new(
        _settings, _settingsService, _spotlightController.IsHotkeyRegistered,
        _updateService, prepareForFirstShow,
        clipboardHotkeyError: () => ClipboardHotkeyRegistrationError,
        invisibleHotkeyRegistered: () => IsInvisibleHotkeyRegistered)
    { Owner = this };

    private void PrepareFirstOpenUi()
    {
        if (_firstOpenUiPrepared || _cleanedUp) return;
        _firstOpenUiPrepared = true;
        PrepareExpandedContentForFirstOpen();
        PrepareSecondaryContentForFirstOpen();
    }

    private void PrepareSecondaryContentForFirstOpen()
    {
        if (SecondaryContent == null) return;
        var visibility = SecondaryContent.Visibility;
        double opacity = SecondaryContent.Opacity;
        double targetWidth = Math.Max(0, ClipboardTrayWidth - SecondaryContent.Margin.Left - SecondaryContent.Margin.Right);
        double targetHeight = Math.Max(0, ClipboardTrayHeight - SecondaryContent.Margin.Top - SecondaryContent.Margin.Bottom);
        try
        {
            SecondaryContent.Opacity = 0;
            SecondaryContent.Visibility = Visibility.Visible;
            SecondaryContent.Width = targetWidth;
            SecondaryContent.Height = targetHeight;
            SecondaryContent.Measure(new Size(targetWidth, targetHeight));
            SecondaryContent.Arrange(new Rect(0, 0, targetWidth, targetHeight));
        }
        finally
        {
            SecondaryContent.Visibility = visibility;
            SecondaryContent.Opacity = opacity;
        }
    }

    private void PrepareExpandedContentForFirstOpen()
    {
        var visibility = ExpandedContent.Visibility;
        double opacity = ExpandedContent.Opacity;
        _shellState.UpdateExpandedWidth(IsNoWidgetMode, _isLyricsActive, _isMusicExpanded);
        if (MediaRightColumn != null) MediaRightColumn.Visibility = IsNoWidgetMode && !_isLyricsActive ? Visibility.Collapsed : Visibility.Visible;
        double targetWidth = Math.Max(0, _expandedWidth - ExpandedContentHorizontalInset);
        double targetHeight = Math.Max(0, _expandedHeight - ExpandedContentVerticalInset);
        try
        {
            ExpandedContent.Opacity = 0;
            ExpandedContent.Visibility = Visibility.Visible;
            ExpandedContent.Width = targetWidth;
            ExpandedContent.Height = targetHeight;
            ExpandedContent.Measure(new Size(targetWidth, targetHeight));
            ExpandedContent.Arrange(new Rect(0, 0, targetWidth, targetHeight));
            PrepareExpandedContentLayoutForReveal();
            EnsureCachedThumbnailExpandTarget();
        }
        finally
        {
            ExpandedContent.Visibility = visibility;
            ExpandedContent.Opacity = opacity;
        }
    }
}
