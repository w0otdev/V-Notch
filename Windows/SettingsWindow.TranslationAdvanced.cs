using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private readonly Dictionary<string, (TextBlock Label, TextBox Input)> _translationAdvancedInputs = new();
    private readonly Dictionary<string, TextBlock> _translationAdvancedGroups = new();
    private static readonly string[] TranslationAdvancedNames =
    [
        "ModelIdleMinutes",
        "CacheMinutes",
        "CacheEntries",
        "GpuLayers",
        "Threads",
        "BatchThreads",
        "ContextSize",
        "BatchSize",
        "OutputTokens",
        "TemperaturePercent",
        "TopPPercent",
        "TopK",
        "TimeoutSeconds",
    ];
    private static PropertyInfo AdvancedProperty(string name) => typeof(NotchSettings).GetProperty("Translation" + name)!;
    private void LoadTranslationAdvanced(NotchSettings settings)
    {
        if (_translationAdvancedInputs.Count == 0)
        {
            foreach (string name in TranslationAdvancedNames)
            {
                string? group = name switch
                {
                    "ModelIdleMinutes" => "memory",
                    "GpuLayers" => "performance",
                    "ContextSize" => "tokens",
                    "TemperaturePercent" => "sampling",
                    "TimeoutSeconds" => "request",
                    _ => null
                };
                if (group != null)
                {
                    var heading = new TextBlock { Style = (Style)FindResource("SettingTitleText"), Margin = new Thickness(0, 20, 0, 8) };
                    _translationAdvancedGroups[group] = heading;
                    TranslationAdvancedFields.Children.Add(heading);
                }
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
                var input = new TextBox
                {
                    Width = 96,
                    MinHeight = 36,
                    Padding = new Thickness(10, 6, 10, 6),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    FontSize = 13,
                    BorderThickness = new Thickness(1)
                };
                input.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
                input.SetResourceReference(Control.ForegroundProperty, "SoftText");
                input.SetResourceReference(Control.BorderBrushProperty, "CardBorder");
                input.SetResourceReference(TextBox.CaretBrushProperty, "SoftText");
                DockPanel.SetDock(input, Dock.Right);
                var label = new TextBlock { Style = (Style)FindResource("SettingHintText"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                row.Children.Add(input); row.Children.Add(label);
                TranslationAdvancedFields.Children.Add(row);
                _translationAdvancedInputs[name] = (label, input);
                input.LostKeyboardFocus += (_, _) =>
                {
                    if (_isLoadingSettings) return;
                    var snapshot = _settings.Clone();
                    ReadTranslationAdvanced(snapshot);
                    LoadTranslationAdvanced(snapshot);
                    PushLivePreview();
                };
            }
        }
        foreach (var entry in _translationAdvancedInputs)
            entry.Value.Input.Text = Convert.ToString(AdvancedProperty(entry.Key).GetValue(settings), CultureInfo.InvariantCulture)!;
        LocalizeTranslationAdvanced();
    }
    private void ReadTranslationAdvanced(NotchSettings snapshot)
    {
        foreach (var entry in _translationAdvancedInputs)
            if (int.TryParse(entry.Value.Input.Text, out int value)) AdvancedProperty(entry.Key).SetValue(snapshot, value);
    }
    private void LocalizeTranslationAdvanced()
    {
        TranslationAdvancedExpander.Header = Loc.Get("translation.advanced");
        TranslationAdvancedHint.Text = Loc.Get("translation.advancedHint");
        TranslationAdvancedReset.Content = Loc.Get("translation.advancedReset");
        foreach (var group in _translationAdvancedGroups)
            group.Value.Text = Loc.Get("translation.advancedGroup." + group.Key);
        foreach (var entry in _translationAdvancedInputs)
        {
            entry.Value.Label.Text = Loc.Get("translation.advanced." + entry.Key);
            System.Windows.Automation.AutomationProperties.SetName(entry.Value.Input, entry.Value.Label.Text);
        }
    }
    private void TranslationAdvancedReset_Click(object sender, RoutedEventArgs e)
    {
        LoadTranslationAdvanced(new NotchSettings());
        if (!_isLoadingSettings) PushLivePreview();
    }
}
