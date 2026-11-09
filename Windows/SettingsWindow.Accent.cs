using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private string _selectedAccent = "default";
    private bool _loadingAccent;

    private void LoadAccentColor()
    {
        _loadingAccent = true;
        try
        {
            _selectedAccent = AccentTheme.GetSwatch(_settings.AccentColor).Id;
            if (AccentColorChoices.Children.Count == 0)
            {
                foreach (var swatch in AccentTheme.Swatches)
                {
                    var choice = new RadioButton
                    {
                        Tag = swatch.Id,
                        GroupName = "AppAccentColor",
                        Background = AccentTheme.Brush(swatch.Color),
                        Style = (Style)FindResource("AccentSwatchStyle"),
                    };
                    choice.Checked += AccentColor_Checked;
                    AccentColorChoices.Children.Add(choice);
                }
            }

            foreach (RadioButton choice in AccentColorChoices.Children)
                choice.IsChecked = (string)choice.Tag == _selectedAccent;
            AccentTheme.Apply(_selectedAccent, animate: IsLoaded);
            LocalizeAccentColor();
        }
        finally { _loadingAccent = false; }
    }

    private void AccentColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingAccent || _isLoadingSettings || sender is not RadioButton { Tag: string id }) return;
        _selectedAccent = id;
        AccentColorValue.Text = Loc.Get("settings.accentColor." + id);
        ApplyPreview(ReadSettingsFromUi());
    }

    private void LocalizeAccentColor()
    {
        AccentColorLabel.Text = Loc.Get("settings.accentColor");
        AccentColorHint.Text = Loc.Get("settings.accentColor.hint");
        AccentColorValue.Text = Loc.Get("settings.accentColor." + _selectedAccent);
        foreach (RadioButton choice in AccentColorChoices.Children)
        {
            string label = Loc.Get("settings.accentColor." + (string)choice.Tag);
            choice.ToolTip = label;
            AutomationProperties.SetName(choice, label);
        }
    }
}
