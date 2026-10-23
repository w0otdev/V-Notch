using System.Windows;
using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private InvisibleHotkeyController? _invisibleHotkey;
    internal bool IsInvisibleHotkeyRegistered => _invisibleHotkey?.IsRegistered == true;
    private InvisibleModeController? _invisibleModeController;

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

    private Task ToggleInvisibleModeAsync()
    {
        if (_cleanedUp) return Task.CompletedTask;
        _invisibleModeController ??= new InvisibleModeController(
            () => _settings,
            ApplyInvisibleMode,
            snapshot => _settingsService.SaveAsync(snapshot),
            ToggleInvisibleInSettingsAsync);
        return _invisibleModeController.ToggleAsync();
    }

    private async Task<bool> ToggleInvisibleInSettingsAsync()
    {
        var settingsWindow = OwnedWindows.OfType<SettingsWindow>().FirstOrDefault();
        if (settingsWindow != null)
        {
            await settingsWindow.ToggleInvisibleFromShortcutAsync();
            return true;
        }
        return false;
    }
}
