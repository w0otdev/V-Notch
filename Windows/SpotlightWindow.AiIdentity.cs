using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private string? _presentedAiProvider;

    private (string Provider, string Model) CurrentAiIdentity()
    {
        if (_aiHistory.Count > 0 && !string.IsNullOrWhiteSpace(_aiConversationProvider))
            return (_aiConversationProvider, _aiConversationModel);
        string provider = _settings.SpotlightAiProvider;
        return (provider, SpotlightAiService.Configuration(_settings, provider).Model);
    }

    private void UpdateAiIdentity()
    {
        var (provider, model) = CurrentAiIdentity();
        string name = SpotlightAiModelName.Format(model, provider);
        string description = string.IsNullOrWhiteSpace(model) ? provider : provider + " · " + model.Trim();
        AiModeLabel.Text = name;
        AiModelPill.ToolTip = description;
        AutomationProperties.SetName(AiModelPill, description);
        AiProviderLogo.Source = AiProviderBrand.For(provider);
        AiWelcomeLogo.Source = AiProviderBrand.For(provider);
        AutomationProperties.SetName(AiProviderLogo, provider);
        AutomationProperties.SetName(AiWelcomeLogo, provider);
        if (_presentedAiProvider != provider && _aiMode) SyncSearchIconState(animate: false);
        _presentedAiProvider = provider;
    }

    private static Image CreateAiBrandImage(string provider, double size)
    {
        var image = new Image
        {
            Source = AiProviderBrand.For(provider), Width = size, Height = size,
            Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        AutomationProperties.SetName(image, provider);
        return image;
    }
}
