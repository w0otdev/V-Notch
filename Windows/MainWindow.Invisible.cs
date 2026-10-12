using System.Windows;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private InvisibleHotkeyController? _invisibleHotkey;
    private bool _savingInvisibleMode;

    private void UpdateInvisibleTrayVisibility()
    {
        if (_settings.InvisibleMode && TrayIcon.ContextMenu != null)
            TrayIcon.ContextMenu.IsOpen = false;
        TrayIcon.Visibility = _settings.InvisibleMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyInvisibleMode()
    {
        WindowAnimationPolicy.SetInvisibleMode(_settings.InvisibleMode);
        UpdateInvisibleTrayVisibility();
    }

    private void ToggleInvisible_Click(object sender, RoutedEventArgs e) => ToggleInvisibleMode();

    private void ToggleInvisibleMode() => ToggleInvisibleModeAsync().SafeFireAndForget("INVISIBLE-TOGGLE");

    private async Task ToggleInvisibleModeAsync()
    {
        if (_cleanedUp || _savingInvisibleMode) return;
        _savingInvisibleMode = true;
        try
        {
            var settingsWindow = OwnedWindows.OfType<SettingsWindow>().FirstOrDefault();
            if (settingsWindow != null)
            {
                await settingsWindow.ToggleInvisibleFromShortcutAsync();
                return;
            }
            _settings.InvisibleMode = !_settings.InvisibleMode;
            ApplyInvisibleMode();
            await _settingsService.SaveAsync(_settings.Clone());
        }
        finally { _savingInvisibleMode = false; }
    }
}
