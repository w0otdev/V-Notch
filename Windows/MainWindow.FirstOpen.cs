using System.Windows;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private SettingsWindow? _preparedSettingsWindow;
    private SettingsWindow? _activeSettingsWindow;
    private NotchSettings? _preparedSettingsSnapshot;
    private bool _firstOpenUiPrepared;

    private SettingsWindow CreateSettingsWindow(bool prepareForFirstShow = false) => new(
        _settings, _settingsService, _spotlightController.IsHotkeyRegistered,
        _updateService, prepareForFirstShow)
    { Owner = this };

    private void PrepareFirstOpenUi()
    {
        if (_firstOpenUiPrepared || _cleanedUp) return;
        _firstOpenUiPrepared = true;
        PrepareExpandedContentForFirstOpen();
        // Pay the one-time XAML/template/native-window cost before the startup
        // entrance begins, while the notch is still waiting for its first layout.
        try
        {
            _preparedSettingsSnapshot = _settings.Clone();
            _preparedSettingsWindow = CreateSettingsWindow(prepareForFirstShow: true);
            _preparedSettingsWindow.PrepareForFirstShow();
        }
        catch (Exception ex)
        {
            DiscardPreparedSettingsWindow();
            RuntimeLog.Warn("FIRST-OPEN", $"Settings preparation skipped: {ex.GetType().Name}");
        }
    }

    private void PrepareExpandedContentForFirstOpen()
    {
        var visibility = ExpandedContent.Visibility;
        double opacity = ExpandedContent.Opacity;
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

    private SettingsWindow TakeSettingsWindow()
    {
        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        var snapshot = _preparedSettingsSnapshot;
        _preparedSettingsSnapshot = null;
        if (prepared == null) return CreateSettingsWindow();

        prepared.ActivatePreparedWindow(
            _settings,
            snapshotMatches: snapshot?.ValueEquals(_settings) == true,
            _spotlightController.IsHotkeyRegistered);
        return prepared;
    }

    private void DiscardPreparedSettingsWindow()
    {
        var active = _activeSettingsWindow;
        _activeSettingsWindow = null;
        active?.Close();

        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        _preparedSettingsSnapshot = null;
        prepared?.Close();
    }
}
