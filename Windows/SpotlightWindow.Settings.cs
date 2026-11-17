using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Models;

namespace VNotch;

public partial class SpotlightWindow
{
    private NotchSettings _settings = new();

    internal void ApplySettings(NotchSettings settings)
    {
        bool languageChanged = !string.Equals(_settings.Language, settings.Language, StringComparison.Ordinal);
        bool restoreSavedChats = !_settings.SaveAiChatHistory && settings.SaveAiChatHistory;
        if (settings.EnableLocalOnlyMode || !settings.AllowOnlineAi)
            CancelAiRequest();
        if (_settings.SaveAiChatHistory != settings.SaveAiChatHistory)
        {
            // Start a fresh in-memory conversation; never persist an incognito chat on re-enable.
            _settings.SaveAiChatHistory = settings.SaveAiChatHistory;
            CancelAiRequest();
            _savedChats.Clear();
            _aiHistory.Clear();
            _chatId = Guid.NewGuid().ToString("N");
            _aiDraftToRestore = null;
            if (_aiMode) SearchBox.Clear();
            CloseAiHistory();
            RenderAiHistory();
        }
        bool aiSelectionChanged = !string.Equals(_settings.SpotlightAiProvider, settings.SpotlightAiProvider, StringComparison.Ordinal)
            || VNotch.Services.Spotlight.SpotlightAiService.Configuration(_settings, _settings.SpotlightAiProvider).Model
                != VNotch.Services.Spotlight.SpotlightAiService.Configuration(settings, settings.SpotlightAiProvider).Model;
        if (aiSelectionChanged)
        {
            CancelAiRequest();
            SaveAiHistory();
            _chatId = Guid.NewGuid().ToString("N");
            _aiHistory.Clear();
            _aiConversationProvider = settings.SpotlightAiProvider;
            _aiConversationModel = VNotch.Services.Spotlight.SpotlightAiService.Configuration(settings, settings.SpotlightAiProvider).Model;
            _aiDraftToRestore = null;
            CloseAiHistory();
            RenderAiHistory();
        }
        _settings = settings.Clone();
        if (restoreSavedChats) LoadAiHistory();
        _viewModel.HistoryEnabled = _settings.EnableSpotlightHistory;
        if (languageChanged)
            RefreshLocalization();
        ApplyShellSkin();
    }

    internal void ApplyWindowShadow(bool enabled)
    {
        if (ShellShadowBorder != null)
            ShellShadowBorder.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (ShellShadowEffect != null)
            ShellShadowEffect.Opacity = enabled ? 0.6 : 0.0;
        if (!enabled && Shell?.Effect is DropShadowEffect)
            Shell.Effect = null;
    }

    internal void ApplyShellSkin()
    {
        ApplyWindowShadow(_settings.EnableShadow);
        if (Shell == null) return;

        Shell.Background = (Brush)FindResource("ShellBrush");
        RestoreShadow();
    }
}
