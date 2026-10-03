using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SettingsWindow
{
    private NotchSettings? _aiDraft;
    private string _aiEditingProvider = "OpenAI";
    private bool _loadingAi;

    private void LoadSpotlightAiSettings(NotchSettings settings)
    {
        _loadingAi = true;
        _aiDraft = settings.Clone();
        SpotlightDefaultModeCombo.SelectedIndex = settings.SpotlightDefaultAi ? 1 : 0;
        SpotlightAiSpeedSlider.Value = Math.Clamp(settings.SpotlightAiWordsPerSecond, 2, 30);
        SpotlightAiProviderCombo.ItemsSource = SpotlightAiService.Providers;
        _aiEditingProvider = SpotlightAiService.Providers.Contains(settings.SpotlightAiProvider) ? settings.SpotlightAiProvider : "OpenAI";
        SpotlightAiProviderCombo.SelectedItem = _aiEditingProvider;
        var config = SpotlightAiService.Configuration(_aiDraft, _aiEditingProvider);
        SpotlightAiKeyBox.Password = config.Key;
        SpotlightAiModelBox.Text = config.Model;
        UpdateCopilotSettings();
        _loadingAi = false;
    }

    private void SpotlightAiProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAi || _aiDraft == null || SpotlightAiProviderCombo.SelectedItem is not string provider) return;
        SpotlightAiService.Configure(_aiDraft, _aiEditingProvider, SpotlightAiKeyBox.Password.Trim(), SpotlightAiModelBox.Text.Trim());
        _loadingAi = true;
        _aiEditingProvider = provider;
        var config = SpotlightAiService.Configuration(_aiDraft, provider);
        SpotlightAiKeyBox.Password = config.Key;
        SpotlightAiModelBox.Text = config.Model;
        UpdateCopilotSettings();
        _loadingAi = false;
        PushLivePreview();
    }

    private void SpotlightAiInput_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loadingAi || _aiDraft == null) return;
        PushLivePreview();
    }

    private void SpotlightAiSpeed_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingAi || _aiDraft == null) return;
        PushLivePreview();
    }

    private void SpotlightDefaultMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAi || _aiDraft == null) return;
        PushLivePreview();
    }

    private void SpotlightAiModel_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingAi || _aiDraft == null) return;
        PushLivePreview();
    }

    private void ReadSpotlightAiSettings(NotchSettings snapshot)
    {
        if (_aiDraft == null) return;
        SpotlightAiService.Configure(_aiDraft, _aiEditingProvider, SpotlightAiKeyBox.Password.Trim(), SpotlightAiModelBox.Text.Trim());
        snapshot.SpotlightDefaultAi = SpotlightDefaultModeCombo.SelectedIndex == 1;
        snapshot.SpotlightAiProvider = _aiEditingProvider;
        snapshot.SpotlightAiWordsPerSecond = (int)SpotlightAiSpeedSlider.Value;
        foreach (string provider in SpotlightAiService.Providers)
        {
            var config = SpotlightAiService.Configuration(_aiDraft, provider);
            SpotlightAiService.Configure(snapshot, provider, config.Key, config.Model);
        }
    }

    private void LocalizeSpotlightAiSettings()
    {
        LocalizeAdditionalPrivacy();
        ((ComboBoxItem)SpotlightDefaultModeCombo.Items[0]).Content = "Spotlight · " + Loc.Get("spotlight.searchMode");
        ((ComboBoxItem)SpotlightDefaultModeCombo.Items[1]).Content = "Spotlight · " + Loc.Get("spotlight.ai.title");
        SpotlightDefaultModeLabel.Text = Loc.Get("settings.spotlight.defaultMode");
        SpotlightAiSpeedSlider.Label = Loc.Get("settings.spotlightAi.speed");
        SpotlightAiSpeedSlider.Unit = Loc.Get("settings.spotlightAi.speedUnit");
        SpotlightAiTitle.Text = Loc.Get("spotlight.ai.title");
        SpotlightAiDisclaimer.Text = Loc.Get("settings.spotlightAi.disclaimer");
        SpotlightAiHint.Text = Loc.Get("settings.spotlightAi.hint");
        SpotlightAiProviderLabel.Text = Loc.Get("settings.spotlightAi.provider");
        SpotlightAiKeyLabel.Text = Loc.Get("settings.spotlightAi.key");
        SpotlightAiModelLabel.Text = Loc.Get("settings.spotlightAi.model");
        UpdateCopilotSettings();
    }

    private void UpdateCopilotSettings()
    {
        bool copilot = _aiEditingProvider == SpotlightAiService.CopilotProvider;
        SpotlightAiHint.Text = Loc.Get(copilot ? "settings.spotlightAi.copilotHint" : "settings.spotlightAi.hint");
        SpotlightAiKeyLabel.Visibility = copilot ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        ((System.Windows.UIElement)SpotlightAiKeyBox.Parent).Visibility = SpotlightAiKeyLabel.Visibility;
        SpotlightAiModelLabel.Text = Loc.Get(copilot ? "settings.spotlightAi.copilotModel" : "settings.spotlightAi.model");
    }
}
