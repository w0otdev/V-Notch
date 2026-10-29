using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private readonly Dictionary<string, (CheckBox Check, TextBlock Hint)> _privacyOptions = new();
    private static readonly string[] PrivacyOptionNames = ["invisible", "ai", "subtitles", "canvas", "weather", "chatHistory"];

    private void LoadAdditionalPrivacy(NotchSettings settings)
    {
        if (_privacyOptions.Count == 0)
        {
            foreach (string name in PrivacyOptionNames)
            {
                var check = new CheckBox();
                var hint = new TextBlock { Style = (Style)FindResource("SettingHintText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                var panel = new StackPanel();
                panel.Children.Add(check);
                panel.Children.Add(hint);
                var row = new Border { Style = (Style)FindResource("SettingRowBorder"), Child = panel };
                (name is "chatHistory" or "invisible" ? PrivacyStorageOptions : PrivacyNetworkSection).Children.Add(row);
                check.Checked += AdditionalPrivacyChanged;
                check.Unchecked += AdditionalPrivacyChanged;
                _privacyOptions[name] = (check, hint);
            }
        }
        _privacyOptions["ai"].Check.IsChecked = settings.AllowOnlineAi;
        _privacyOptions["invisible"].Check.IsChecked = settings.InvisibleMode;
        _privacyOptions["subtitles"].Check.IsChecked = settings.AllowOnlineSubtitles;
        _privacyOptions["canvas"].Check.IsChecked = settings.AllowOnlineCanvas;
        _privacyOptions["weather"].Check.IsChecked = settings.AllowOnlineWeather;
        _privacyOptions["chatHistory"].Check.IsChecked = settings.SaveAiChatHistory;
        LocalizeAdditionalPrivacy();
    }

    private void AdditionalPrivacyChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || _isUpdatingSpotifyCanvasOptIn) return;
        if (_privacyOptions.TryGetValue("canvas", out var option) && ReferenceEquals(sender, option.Check))
            SetSpotifyCanvasOptIn(option.Check.IsChecked == true, ConfirmSpotifyCanvasOptIn);
        else PushLivePreview();
    }

    private void ReadAdditionalPrivacy(NotchSettings settings)
    {
        if (_privacyOptions.Count == 0) return;
        settings.AllowOnlineAi = _privacyOptions["ai"].Check.IsChecked == true;
        settings.InvisibleMode = _privacyOptions["invisible"].Check.IsChecked == true;
        settings.AllowOnlineSubtitles = _privacyOptions["subtitles"].Check.IsChecked == true;
        settings.AllowOnlineCanvas = _privacyOptions["canvas"].Check.IsChecked == true;
        settings.AllowOnlineWeather = _privacyOptions["weather"].Check.IsChecked == true;
        settings.SaveAiChatHistory = _privacyOptions["chatHistory"].Check.IsChecked == true;
    }

    private void LocalizeAdditionalPrivacy()
    {
        foreach (var (name, controls) in _privacyOptions)
        {
            string key = "settings.privacy." + name;
            controls.Check.Content = Loc.Get(key);
            controls.Hint.Text = Loc.Get(key + ".hint");
            if (name == "invisible")
                controls.Hint.Text += " " + Loc.Get(_invisibleHotkeyRegistered?.Invoke() == true
                    ? "settings.privacy.invisible.shortcut" : "settings.privacy.invisible.shortcutUnavailable");
        }
    }

    internal async Task ToggleInvisibleFromShortcutAsync()
    {
        var check = _privacyOptions["invisible"].Check;
        bool enabled = check.IsChecked != true;
        check.IsChecked = enabled;
        _livePreviewDebounce?.Stop();
        ApplyPreview(ReadSettingsFromUi());
        // Persist only this external command; keep other unsaved edits cancellable.
        var baseline = _editSession.Baseline;
        baseline.InvisibleMode = enabled;
        await _settingsAppService.ApplyAsync(baseline, _translationDownload?.Token ?? CancellationToken.None);
        _editSession.CommitInvisibleMode(enabled);
    }
}
