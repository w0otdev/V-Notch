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
    private bool _settingsPreparationScheduled;
    private int _settingsPreparationGeneration;

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

    private void ScheduleSettingsFirstOpenPreparation()
    {
        if (_settingsPreparationScheduled || _cleanedUp) return;
        _settingsPreparationScheduled = true;
        int generation = _settingsPreparationGeneration;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            PrepareSettingsFirstOpenAsync(generation).SafeFireAndForget("SETTINGS-PREPARE")));
    }

    private async Task PrepareSettingsFirstOpenAsync(int generation)
    {
        if (_cleanedUp || generation != _settingsPreparationGeneration || _activeSettingsWindow != null) return;
        SettingsWindow? prepared = null;
        try
        {
            // Both stages run after the notch's entrance. Yield between XAML
            // construction and native template realization so input can run.
            _preparedSettingsSnapshot = _settings.Clone();
            prepared = CreateSettingsWindow(prepareForFirstShow: true);
            _preparedSettingsWindow = prepared;
            await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (_cleanedUp || generation != _settingsPreparationGeneration || !ReferenceEquals(prepared, _preparedSettingsWindow)) return;
            await prepared.PrepareForFirstShowAsync();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(prepared, _preparedSettingsWindow))
            {
                _preparedSettingsWindow = null;
                _preparedSettingsSnapshot = null;
                prepared?.Close();
            }
            RuntimeLog.Warn("FIRST-OPEN", $"Settings preparation skipped: {ex.GetType().Name}");
        }
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

    private SettingsWindow TakeSettingsWindow()
    {
        // Cancel queued stages before taking ownership. A user opening Settings
        // while its hidden realization yields must never have it hidden again.
        _settingsPreparationGeneration++;
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
        _settingsPreparationGeneration++;
        var active = _activeSettingsWindow;
        _activeSettingsWindow = null;
        active?.Close();

        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        _preparedSettingsSnapshot = null;
        prepared?.Close();
    }
}
