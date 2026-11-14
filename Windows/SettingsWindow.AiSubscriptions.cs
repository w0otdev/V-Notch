using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch;

public partial class SettingsWindow
{
    private bool _aiUsageCustomizationReady;
    private bool _updatingAiUsageCustomization;
    private bool _hasAiUsageAppearancePreview;
    private string _aiUsageCodexColor = "#00D491";
    private string _aiUsageClaudeColor = "#D97757";

    private void LoadAiUsageCustomization(NotchSettings settings)
    {
        if (!_aiUsageCustomizationReady) AiUsageTypography.Apply(AiUsageCard);
        _updatingAiUsageCustomization = true;
        try
        {
            AiUsageShowCodexCheck.IsChecked = settings.AiUsageShowCodex;
            AiUsageShowClaudeCheck.IsChecked = settings.AiUsageShowClaude;
            AiUsageWeeklyCheck.IsChecked = settings.AiUsageShowWeekly;
            AiUsageResetsCheck.IsChecked = settings.AiUsageShowResets;
            AiUsagePlanCheck.IsChecked = settings.AiUsageShowPlan;
            AiUsageLogosCheck.IsChecked = settings.AiUsageShowLogos;
            AiUsageAccountCheck.IsChecked = settings.AiUsageShowAccount;
            AiUsageCodexFirstChoice.IsChecked = !settings.AiUsageClaudeFirst;
            AiUsageClaudeFirstChoice.IsChecked = settings.AiUsageClaudeFirst;
            AiUsageComfortableChoice.IsChecked = !settings.AiUsageCompact;
            AiUsageCompactChoice.IsChecked = settings.AiUsageCompact;
            AiUsageRemainingChoice.IsChecked = !settings.AiUsageShowUsedPercent;
            AiUsageUsedChoice.IsChecked = settings.AiUsageShowUsedPercent;
            AiUsageStatusColorsChoice.IsChecked = settings.AiUsageStatusColors;
            AiUsageCustomColorsChoice.IsChecked = !settings.AiUsageStatusColors;
            AiUsageBarHeightSlider.Value = settings.AiUsageBarHeight;
            AiUsageMotionSlider.Value = settings.AiUsageMotionIntensity;
            _aiUsageCodexColor = settings.AiUsageCodexColor;
            _aiUsageClaudeColor = settings.AiUsageClaudeColor;
            AiUsageCodexColorInput.Text = settings.AiUsageCodexColor;
            AiUsageClaudeColorInput.Text = settings.AiUsageClaudeColor;
            ClearAiUsageColorError(AiUsageCodexColorInput, AiUsageCodexColorError);
            ClearAiUsageColorError(AiUsageClaudeColorInput, AiUsageClaudeColorError);
            UpdateAiUsageColorAvailability();
            _aiUsageCustomizationReady = true;
        }
        finally { _updatingAiUsageCustomization = false; }
    }

    private void ReadAiUsageCustomization(NotchSettings snapshot)
    {
        if (!_aiUsageCustomizationReady) return;
        snapshot.AiUsageShowCodex = AiUsageShowCodexCheck.IsChecked == true;
        snapshot.AiUsageShowClaude = AiUsageShowClaudeCheck.IsChecked == true;
        snapshot.AiUsageShowWeekly = AiUsageWeeklyCheck.IsChecked == true;
        snapshot.AiUsageShowResets = AiUsageResetsCheck.IsChecked == true;
        snapshot.AiUsageShowPlan = AiUsagePlanCheck.IsChecked == true;
        snapshot.AiUsageShowLogos = AiUsageLogosCheck.IsChecked == true;
        snapshot.AiUsageShowAccount = AiUsageAccountCheck.IsChecked == true;
        snapshot.AiUsageClaudeFirst = AiUsageClaudeFirstChoice.IsChecked == true;
        snapshot.AiUsageCompact = AiUsageCompactChoice.IsChecked == true;
        snapshot.AiUsageShowUsedPercent = AiUsageUsedChoice.IsChecked == true;
        snapshot.AiUsageStatusColors = AiUsageCustomColorsChoice.IsChecked != true;
        snapshot.AiUsageBarHeight = (int)AiUsageBarHeightSlider.Value;
        snapshot.AiUsageMotionIntensity = (int)AiUsageMotionSlider.Value;
        snapshot.AiUsageCodexColor = _aiUsageCodexColor;
        snapshot.AiUsageClaudeColor = _aiUsageClaudeColor;
    }

    private void LocalizeAiUsageCustomization()
    {
        foreach (var (control, key) in new (TextBlock, string)[]
        {
            (AiUsageConnectionsTitle, "connections"), (AiUsageCustomizeHint, "customizeHint"), (AiUsageSampleLabel, "samplePreview"),
            (AiUsageOrderLabel, "order"), (AiUsageDensityLabel, "density"), (AiUsageMetricLabel, "metric"),
            (AiUsagePaletteLabel, "palette"), (AiUsageBarHeightLabel, "barHeight"), (AiUsageMotionLabel, "motion"),
            (AiUsageDetailsTitle, "visibleDetails"), (AiUsageColorsTitle, "colorsAndBars"), (AiUsageCustomizeTitle, "customize")
        }) control.Text = Loc.Get("aiUsage." + key);
        foreach (var (control, key) in new (CheckBox, string)[]
        {
            (AiUsageShowCodexCheck, "showCodex"), (AiUsageShowClaudeCheck, "showClaude"), (AiUsageWeeklyCheck, "showWeekly"),
            (AiUsageResetsCheck, "showResets"), (AiUsagePlanCheck, "showPlan"), (AiUsageLogosCheck, "showLogos"), (AiUsageAccountCheck, "showAccount")
        }) control.Content = Loc.Get("aiUsage." + key);
        foreach (var (choice, key) in new (RadioButton, string)[]
        {
            (AiUsageComfortableChoice, "comfortable"), (AiUsageCompactChoice, "compact"),
            (AiUsageRemainingChoice, "remaining"), (AiUsageUsedChoice, "usedMetric"),
            (AiUsageStatusColorsChoice, "statusColors"), (AiUsageCustomColorsChoice, "customColors")
        }) choice.Content = Loc.Get("aiUsage." + key);
        AiUsageResetAppearanceButton.Content = Loc.Get("aiUsage.resetAppearance");
        AiUsageColorHint.Text = Loc.Get("aiUsage.colorHint");
        foreach (var (input, error) in new[] { (AiUsageCodexColorInput, AiUsageCodexColorError), (AiUsageClaudeColorInput, AiUsageClaudeColorError) })
            if (error.Visibility == Visibility.Visible) ShowAiUsageColorError(input, error);
        AiUsageCustomizationPreview.PresentExample();
    }

    private void PublishAiUsageCustomization()
    {
        if (_isLoadingSettings || !_aiUsageCustomizationReady || _updatingAiUsageCustomization) return;
        var snapshot = ReadSettingsFromUi();
        AiUsageAppearance.Apply(snapshot);
        _hasAiUsageAppearancePreview = AiUsageAppearance.From(snapshot) != AiUsageAppearance.From(_editSession.Baseline);
        AiUsageCustomizationPreview.PresentExample();
        AiUsageSettingsPreview.Present();
        PushLivePreview();
    }

    private void AiUsageCustomize_Changed(object sender, RoutedEventArgs e)
    {
        if (_aiUsageCustomizationReady) UpdateAiUsageColorAvailability();
        PublishAiUsageCustomization();
    }
    private void AiUsageCustomizationSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => PublishAiUsageCustomization();
    private void AiUsageResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        LoadAiUsageCustomization(new NotchSettings());
        PublishAiUsageCustomization();
    }

    private void UpdateAiUsageColorAvailability()
    {
        bool custom = AiUsageCustomColorsChoice.IsChecked == true;
        bool reveal = custom && !AiUsageCustomColorsPanel.IsEnabled;
        AiUsageCustomColorsPanel.IsEnabled = custom;
        if (reveal && !_isLoadingSettings && !_updatingAiUsageCustomization) RevealSettingsRows(AiUsageCustomColorsPanel);
    }

    // No TextChanged validation: editing is quiet; only leaving the field commits it.
    private void AiUsageColor_Blur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isLoadingSettings || !_aiUsageCustomizationReady || sender is not TextBox input || !input.IsEnabled || AiUsageCustomColorsChoice.IsChecked != true) return;
        bool codex = ReferenceEquals(input, AiUsageCodexColorInput);
        var error = codex ? AiUsageCodexColorError : AiUsageClaudeColorError;
        string hex = input.Text.Trim();
        if (hex.Length != 7 || hex[0] != '#' || !hex.Skip(1).All(char.IsAsciiHexDigit))
        {
            ShowAiUsageColorError(input, error);
            return;
        }
        CommitAiUsageColor(input, error, codex, hex);
    }

    private void CommitAiUsageColor(TextBox input, TextBlock error, bool codex, string hex)
    {
        input.Text = hex.ToUpperInvariant();
        ClearAiUsageColorError(input, error);
        if (codex) _aiUsageCodexColor = input.Text;
        else _aiUsageClaudeColor = input.Text;
        PublishAiUsageCustomization();
    }

    private static void ShowAiUsageColorError(TextBox input, TextBlock error)
    {
        input.Tag = "invalid";
        error.Text = "! " + Loc.Get("aiUsage.colorError");
        error.Visibility = Visibility.Visible;
        AutomationProperties.SetItemStatus(input, error.Text);
        AutomationProperties.SetHelpText(input, error.Text);
        UIElementAutomationPeer.FromElement(error)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static void ClearAiUsageColorError(TextBox input, TextBlock error)
    {
        input.Tag = null;
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetItemStatus(input, "");
        AutomationProperties.SetHelpText(input, Loc.Get("aiUsage.colorHint"));
    }

}
