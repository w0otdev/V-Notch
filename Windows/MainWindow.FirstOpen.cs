using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private SettingsWindow? _preparedSettingsWindow;
    private NotchSettings? _preparedSettingsSnapshot;
    private bool _firstOpenUiPrepared;

    private SettingsWindow CreateSettingsWindow(bool prepareForFirstShow = false) => new(
        _settings, _settingsService, _bluetoothModule, _spotlightController.IsHotkeyRegistered,
        _updateService, prepareForFirstShow)
    { Owner = this };

    private void PrepareFirstOpenUi()
    {
        if (_firstOpenUiPrepared || _cleanedUp) return;
        _firstOpenUiPrepared = true;
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

    private SettingsWindow TakeSettingsWindow()
    {
        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        var snapshot = _preparedSettingsSnapshot;
        _preparedSettingsSnapshot = null;
        if (prepared == null) return CreateSettingsWindow();
        if (snapshot?.ValueEquals(_settings) != true)
            prepared.RefreshPreparedSettings(_settings, _spotlightController.IsHotkeyRegistered);
        else
            prepared.SetSpotlightHotkeyStatus(_spotlightController.IsHotkeyRegistered);
        return prepared;
    }

    private void DiscardPreparedSettingsWindow()
    {
        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        _preparedSettingsSnapshot = null;
        prepared?.Close();
    }
}
