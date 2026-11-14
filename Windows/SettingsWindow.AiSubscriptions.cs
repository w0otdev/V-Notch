using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.AiUsage;

namespace VNotch;

public partial class SettingsWindow
{
    private static readonly Color AiUsagePreviewRestBorderColor = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);

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
        SyncAiUsageSampleDetails(animate: false);
    }

    private void PublishAiUsageCustomization(FrameworkElement? anchor = null)
    {
        if (_isLoadingSettings || !_aiUsageCustomizationReady || _updatingAiUsageCustomization) return;
        RunWithAiUsageScrollAnchor(anchor, ApplyAndPresentAiUsageCustomization);
    }

    private void ApplyAndPresentAiUsageCustomization()
    {
        if (_isLoadingSettings || !_aiUsageCustomizationReady || _updatingAiUsageCustomization) return;
        var snapshot = ReadSettingsFromUi();
        AiUsageAppearance.Apply(snapshot);
        _hasAiUsageAppearancePreview = AiUsageAppearance.From(snapshot) != AiUsageAppearance.From(_editSession.Baseline);
        AiUsageCustomizationPreview.PresentExample();
        AiUsageSettingsPreview.Present();
        TriggerAiUsagePreviewFeedback();
        PushLivePreview();
    }

    private void RunWithAiUsageScrollAnchor(FrameworkElement? anchor, Action update)
    {
        FrameworkElement? target = anchor is { IsVisible: true } ? anchor
            : (AiUsageCustomizationTree is { IsVisible: true } ? AiUsageCustomizationTree : null);
        bool canAnchor = target != null
            && SettingsScrollViewer != null
            && IsLoaded
            && SettingsScrollViewer.IsAncestorOf(target);

        double beforeY = canAnchor ? target!.TranslatePoint(new Point(0, 0), SettingsScrollViewer!).Y : 0;

        update();

        if (!canAnchor)
        {
            UpdateAiUsageStickyPreview();
            return;
        }

        SettingsScrollViewer!.UpdateLayout();
        if (target!.IsVisible && SettingsScrollViewer.IsAncestorOf(target))
        {
            double deltaY = target.TranslatePoint(new Point(0, 0), SettingsScrollViewer).Y - beforeY;
            if (Math.Abs(deltaY) > 0.25)
            {
                double maxScroll = SettingsScrollViewer.ScrollableHeight;
                double adjusted = Math.Clamp(SettingsScrollViewer.VerticalOffset + deltaY, 0, maxScroll);
                if (_isScrollAnimating)
                    _scrollTarget = Math.Clamp(_scrollTarget + deltaY, 0, maxScroll);
                SettingsScrollViewer.ScrollToVerticalOffset(adjusted);
                SettingsScrollViewer.UpdateLayout();
            }
        }

        UpdateAiUsageStickyPreview();
    }

    private void UpdateAiUsageStickyPreview()
    {
        if (AiUsagePreviewStickyTransform == null || AiUsageStudioGrid == null ||
            AiUsageCustomizationPreviewCard == null || SettingsScrollViewer == null)
            return;

        bool active = IsLoaded && PanelAiUsage?.IsVisible == true &&
            AiUsageStudioGrid.IsVisible && SettingsScrollViewer.IsAncestorOf(AiUsageStudioGrid);
        if (!active)
        {
            AiUsagePreviewStickyTransform.Y = 0;
            return;
        }

        const double StickyTopInset = 12.0;
        double gridTopInViewport = AiUsageStudioGrid.TranslatePoint(new Point(0, 0), SettingsScrollViewer).Y;
        double naturalCardTopInGrid = AiUsageCustomizationPreviewCard.Margin.Top;
        double naturalCardTopInViewport = gridTopInViewport + naturalCardTopInGrid;
        double maxStickyTravel = Math.Max(0,
            AiUsageStudioGrid.ActualHeight - naturalCardTopInGrid - AiUsageCustomizationPreviewCard.ActualHeight);

        AiUsagePreviewStickyTransform.Y = Math.Clamp(StickyTopInset - naturalCardTopInViewport, 0, maxStickyTravel);
    }

    private void SyncAiUsageSampleDetails(bool animate)
    {
        if (AiUsageSampleDetailsText == null || AiUsageCustomizationPreview == null) return;
        string summary = AiUsageCustomizationPreview.PreviewTooltipSummary;
        bool changed = !string.Equals(AiUsageSampleDetailsText.Text, summary, StringComparison.Ordinal);
        AiUsageSampleDetailsText.Text = summary;
        AiUsageSampleDetailsText.Visibility = string.IsNullOrEmpty(summary) ? Visibility.Collapsed : Visibility.Visible;

        if (!changed || !animate || string.IsNullOrEmpty(summary) ||
            AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation ||
            AiUsageAppearance.Current.MotionIntensity <= 0)
        {
            return;
        }

        var ease = TryFindResource("InteractiveEaseOut") as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(220);

        var fade = new DoubleAnimation(0.3, 0.82, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        AiUsageSampleDetailsText.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);

        if (AiUsageSampleDetailsTranslate != null)
        {
            var slide = new DoubleAnimation(4, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
            AiUsageSampleDetailsTranslate.BeginAnimation(TranslateTransform.YProperty, slide, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void TriggerAiUsagePreviewFeedback()
    {
        SyncAiUsageSampleDetails(animate: true);
        if (AiUsagePreviewCardBorder == null || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation ||
            AiUsageAppearance.Current.MotionIntensity <= 0)
            return;

        Color accent = TryFindResource("AccentBrushStrong") is SolidColorBrush b
            ? Color.FromArgb(0x90, b.Color.R, b.Color.G, b.Color.B)
            : Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);

        var ease = TryFindResource("InteractiveEaseOut") as IEasingFunction ?? new CubicEase { EasingMode = EasingMode.EaseOut };
        var anim = new ColorAnimation(accent, AiUsagePreviewRestBorderColor, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        AiUsagePreviewCardBorder.BeginAnimation(SolidColorBrush.ColorProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void AiUsageCustomize_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || _updatingAiUsageCustomization) return;
        RunWithAiUsageScrollAnchor(sender as FrameworkElement, () =>
        {
            if (_aiUsageCustomizationReady) UpdateAiUsageColorAvailability();
            ApplyAndPresentAiUsageCustomization();
        });
    }
    private void AiUsageCustomizationSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        => PublishAiUsageCustomization(sender as FrameworkElement);
    private void AiUsageResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        RunWithAiUsageScrollAnchor(sender as FrameworkElement, () =>
        {
            LoadAiUsageCustomization(new NotchSettings());
            ApplyAndPresentAiUsageCustomization();
        });
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
        PublishAiUsageCustomization(input);
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
