using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    #region Liquid Glass skin

    private const string GlassPresetDark = "dark";
    private const string GlassPresetUltraThin = "ultrathin";
    private const string GlassPresetThin = "thin";
    private const string GlassPresetThick = "thick";
    private const string GlassPresetUltraThick = "ultrathick";

    // Snapshot of the user's manually-tuned glass values, preserved so the
    private Models.LiquidGlassConfig? _customGlassSnapshot;
    private bool _suppressGlassPresetChange;

    private static Models.LiquidGlassConfig FrostedGlassPreset() => new()
    {
        BlurAmount = 0.20,
        Refraction = 0.6,
        EdgeBend = 1.0,
        ChromaticAberration = 0.06,
        EdgeHighlight = 0.30,
        TouchLight = 0.9,
        Specular = 0.0,
        Fresnel = 0.0,
        Distortion = 0.03,
        ZRadius = 0.35,
        Opacity = 1.0,
        Saturation = 0.0,
        Brightness = 0.0,
        ShadowOpacity = 0.50,
        ShadowSpread = 18,
        BevelMode = 0,
        Variant = 0,
        PowerFactor = 3.0,
        RefractionA = 0.7,
        RefractionB = 2.3,
        RefractionC = 5.2,
        RefractionD = 6.9,
        FPower = 1.0,
        Noise = 0.085,
        GlowWeight = 0.60,
        GlowBias = -0.02,
        GlowEdge0 = 0.40,
        GlowEdge1 = -0.40
    };

    private static Models.LiquidGlassConfig DarkGlassPreset() => new()
    {
        BlurAmount = 0.25,
        Refraction = 1.0,
        EdgeBend = 1.2,
        ChromaticAberration = 0.10,
        EdgeHighlight = 0.12,
        TouchLight = 0.9,
        Specular = 0.0,
        Fresnel = 0.0,
        Distortion = 0.06,
        ZRadius = 0.45,
        Opacity = 1.0,
        Saturation = -0.20,
        Brightness = -0.15,
        ShadowOpacity = 0.80,
        ShadowSpread = 30,
        BevelMode = 1,
        Variant = 0,
        PowerFactor = 3.0,
        RefractionA = 0.7,
        RefractionB = 2.3,
        RefractionC = 5.2,
        RefractionD = 6.9,
        FPower = 1.0,
        Noise = 0.060,
        GlowWeight = 0.50,
        GlowBias = -0.06,
        GlowEdge0 = 0.35,
        GlowEdge1 = -0.35
    };

    private static Models.LiquidGlassConfig RegularGlassPreset() => new()
    {
        BlurAmount = 0.20,
        Refraction = 0.7,
        EdgeBend = 1.0,
        ChromaticAberration = 0.08,
        EdgeHighlight = 0.25,
        TouchLight = 0.9,
        Specular = 0.0,
        Fresnel = 0.0,
        Distortion = 0.04,
        ZRadius = 0.38,
        Opacity = 1.0,
        Saturation = 0.0,
        Brightness = 0.0,
        ShadowOpacity = 0.65,
        ShadowSpread = 20,
        BevelMode = 0,
        Variant = 0,
        PowerFactor = 3.0,
        RefractionA = 0.7,
        RefractionB = 2.3,
        RefractionC = 5.2,
        RefractionD = 6.9,
        FPower = 1.0,
        Noise = 0.066,
        GlowWeight = 0.692,
        GlowBias = -0.040,
        GlowEdge0 = 0.441,
        GlowEdge1 = -0.474
    };

    private static Models.LiquidGlassConfig ClearGlassPreset() => new()
    {
        BlurAmount = 0.05,
        Refraction = 0.15,
        EdgeBend = 1.25,
        ChromaticAberration = 0.04,
        EdgeHighlight = 0.30,
        TouchLight = 0.9,
        Specular = 0.35,
        Fresnel = 0.45,
        Distortion = 0.01,
        Noise = 0.0,
        ZRadius = 0.15,
        Opacity = 1.0,
        Saturation = 0.05,
        Brightness = 0.05,
        ShadowOpacity = 0.20,
        ShadowSpread = 10,
        BevelMode = 0,
        Variant = 1
    };

    private static Models.LiquidGlassConfig UltraThinGlassPreset() => new()
    {
        BlurAmount = 0.10,
        Refraction = 0.20,
        EdgeBend = 1.10,
        ChromaticAberration = 0.04,
        EdgeHighlight = 0.20,
        TouchLight = 0.9,
        Specular = 0.35,
        Fresnel = 0.25,
        Distortion = 0.015,
        Noise = 0.04,
        ZRadius = 0.10,
        Opacity = 1.0,
        Saturation = -0.05,
        Brightness = 0.0,
        ShadowOpacity = 0.30,
        ShadowSpread = 10,
        BevelMode = 0,
        Variant = 0
    };

    private static Models.LiquidGlassConfig ThinGlassPreset() => new()
    {
        BlurAmount = 0.20,
        Refraction = 0.30,
        EdgeBend = 1.30,
        ChromaticAberration = 0.05,
        EdgeHighlight = 0.22,
        TouchLight = 0.9,
        Specular = 0.32,
        Fresnel = 0.30,
        Distortion = 0.02,
        Noise = 0.06,
        ZRadius = 0.15,
        Opacity = 1.0,
        Saturation = -0.10,
        Brightness = -0.05,
        ShadowOpacity = 0.55,
        ShadowSpread = 15,
        BevelMode = 0,
        Variant = 0
    };

    private static Models.LiquidGlassConfig ThickGlassPreset() => new()
    {
        BlurAmount = 0.35,
        Refraction = 0.8,
        EdgeBend = 1.75,
        ChromaticAberration = 0.10,
        EdgeHighlight = 0.28,
        TouchLight = 0.9,
        Specular = 0.28,
        Fresnel = 0.40,
        Distortion = 0.05,
        Noise = 0.10,
        ZRadius = 0.48,
        Opacity = 1.0,
        Saturation = -0.20,
        Brightness = -0.15,
        ShadowOpacity = 0.75,
        ShadowSpread = 25,
        BevelMode = 0,
        Variant = 0
    };

    private static Models.LiquidGlassConfig UltraThickGlassPreset() => new()
    {
        BlurAmount = 0.50,
        Refraction = 0.9,
        EdgeBend = 1.90,
        ChromaticAberration = 0.12,
        EdgeHighlight = 0.32,
        TouchLight = 0.9,
        Specular = 0.25,
        Fresnel = 0.45,
        Distortion = 0.06,
        Noise = 0.12,
        ZRadius = 0.60,
        Opacity = 1.0,
        Saturation = -0.25,
        Brightness = -0.20,
        ShadowOpacity = 0.85,
        ShadowSpread = 32,
        BevelMode = 0,
        Variant = 0
    };

    private void EnsureGlassPresetItems()
    {
        if (GlassPresetCombo.Items.Count > 0) return;
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.custom"), Tag = GlassPresetCustom });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.frosted"), Tag = GlassPresetFrosted });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.dark"), Tag = GlassPresetDark });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.ultrathin"), Tag = GlassPresetUltraThin });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.thin"), Tag = GlassPresetThin });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.regular"), Tag = GlassPresetRegular });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.thick"), Tag = GlassPresetThick });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.ultrathick"), Tag = GlassPresetUltraThick });
        GlassPresetCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.glass.preset.clear"), Tag = GlassPresetClear });
    }

    private Models.LiquidGlassConfig ReadGlassConfigFromSliders()
    {
        var c = (_settings.LiquidGlass ?? new Models.LiquidGlassConfig()).Clone();
        c.BlurAmount = GlassBlurSlider.Value / 100.0;
        c.Refraction = GlassRefractionSlider.Value / 100.0;
        c.EdgeBend = GlassEdgeBendSlider.Value / 100.0;
        c.ChromaticAberration = GlassChromSlider.Value / 100.0;
        c.EdgeHighlight = GlassEdgeHighlightSlider.Value / 100.0;
        c.TouchLight = GlassTouchLightSlider.Value / 100.0;
        c.Specular = GlassSpecularSlider.Value / 100.0;
        c.Fresnel = GlassFresnelSlider.Value / 100.0;
        c.Distortion = GlassDistortionSlider.Value / 100.0;
        c.Noise = GlassGrainSlider.Value / 100.0;
        c.ZRadius = GlassZRadiusSlider.Value / 100.0;
        c.Opacity = GlassOpacitySlider.Value / 100.0;
        c.Saturation = GlassSaturationSlider.Value / 100.0;
        c.Brightness = GlassBrightnessSlider.Value / 100.0;
        c.ShadowOpacity = GlassShadowOpacitySlider.Value / 100.0;
        c.ShadowSpread = (int)Math.Round(GlassShadowSpreadSlider.Value);
        c.BevelMode = (int)Math.Round(GlassBevelModeSlider.Value);
        c.TargetFps = (int)Math.Round(GlassFpsSlider.Value);
        return c;
    }

    private void ApplyGlassConfigToSliders(Models.LiquidGlassConfig c)
    {
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            GlassBlurSlider.Value = Math.Round(c.BlurAmount * 100);
            GlassRefractionSlider.Value = Math.Round(c.Refraction * 100);
            GlassEdgeBendSlider.Value = Math.Round(c.EdgeBend * 100);
            GlassChromSlider.Value = Math.Round(c.ChromaticAberration * 100);
            GlassEdgeHighlightSlider.Value = Math.Round(c.EdgeHighlight * 100);
            GlassTouchLightSlider.Value = Math.Round(c.TouchLight * 100);
            GlassSpecularSlider.Value = Math.Round(c.Specular * 100);
            GlassFresnelSlider.Value = Math.Round(c.Fresnel * 100);
            GlassDistortionSlider.Value = Math.Round(c.Distortion * 100);
            GlassGrainSlider.Value = Math.Round(c.Noise * 100);
            GlassZRadiusSlider.Value = Math.Round(c.ZRadius * 100);
            GlassOpacitySlider.Value = Math.Round(c.Opacity * 100);
            GlassSaturationSlider.Value = Math.Round(c.Saturation * 100);
            GlassBrightnessSlider.Value = Math.Round(c.Brightness * 100);
            GlassShadowOpacitySlider.Value = Math.Round(c.ShadowOpacity * 100);
            GlassShadowSpreadSlider.Value = c.ShadowSpread;
            GlassBevelModeSlider.Value = c.BevelMode;
            GlassFpsSlider.Value = c.TargetFps <= 0 ? 0 : c.TargetFps;
        }
        finally
        {
            _isLoadingSettings = prev;
        }
    }

    private void SelectGlassPreset(string tag)
    {
        EnsureGlassPresetItems();
        for (int i = 0; i < GlassPresetCombo.Items.Count; i++)
        {
            if (GlassPresetCombo.Items[i] is System.Windows.Controls.ComboBoxItem item &&
                (item.Tag as string) == tag)
            {
                _suppressGlassPresetChange = true;
                GlassPresetCombo.SelectedIndex = i;
                _suppressGlassPresetChange = false;
                return;
            }
        }
    }

    private void LoadLiquidGlassUi()
    {
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            if (SkinCombo.Items.Count == 0)
            {
                PopulateSkinItems();
            }

            EnsureGlassPresetItems();

            bool glass = string.Equals(_settings.NotchStyle, SkinLiquidGlass, StringComparison.OrdinalIgnoreCase);
            SkinCombo.SelectedIndex = glass ? 1 : 0;

            var c = _settings.LiquidGlass ?? new Models.LiquidGlassConfig();
            ApplyGlassConfigToSliders(c);
            if (GpuRefractionCheck != null)
                GpuRefractionCheck.IsChecked = c.UseGpuRefraction;
            if (UltimateGpuCheck != null)
            {
                UltimateGpuCheck.IsChecked = c.UseUltimateGpu;
                UltimateGpuCheck.IsEnabled = c.UseGpuRefraction;
                UltimateGpuCheck.Opacity = c.UseGpuRefraction ? 1.0 : 0.4;
            }
            if (UltimateGpuWarning != null)
                UltimateGpuWarning.Visibility = c.UseUltimateGpu ? Visibility.Visible : Visibility.Collapsed;

            // The user's tuned values live in their own persistent slot. If it's
            _settings.LiquidGlassCustom ??= c.Clone();
            _editSession.EnsureCustomGlass(_settings.LiquidGlassCustom);
            _customGlassSnapshot = _settings.LiquidGlassCustom.Clone();

            string preset = string.IsNullOrWhiteSpace(_settings.LiquidGlassPreset) ? GlassPresetCustom : _settings.LiquidGlassPreset;
            SelectGlassPreset(preset);

            LiquidGlassConfigPanel.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isLoadingSettings = prev;
        }
    }

    private void ReadLiquidGlassUi(NotchSettings target)
    {
        string requestedStyle =
            (SkinCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "default";
        target.NotchStyle = string.Equals(requestedStyle, SkinLiquidGlass, StringComparison.OrdinalIgnoreCase)
            ? SkinLiquidGlass
            : "default";

        var c = target.LiquidGlass ??= new Models.LiquidGlassConfig();
        var ui = ReadGlassConfigFromSliders();
        c.BlurAmount = ui.BlurAmount;
        c.Refraction = ui.Refraction;
        c.EdgeBend = ui.EdgeBend;
        c.ChromaticAberration = ui.ChromaticAberration;
        c.EdgeHighlight = ui.EdgeHighlight;
        c.TouchLight = ui.TouchLight;
        c.Specular = ui.Specular;
        c.Fresnel = ui.Fresnel;
        c.Distortion = ui.Distortion;
        c.Noise = ui.Noise;
        c.ZRadius = ui.ZRadius;
        c.Opacity = ui.Opacity;
        c.Saturation = ui.Saturation;
        c.Brightness = ui.Brightness;
        c.ShadowOpacity = ui.ShadowOpacity;
        c.ShadowSpread = ui.ShadowSpread;
        c.BevelMode = ui.BevelMode;
        c.TargetFps = ui.TargetFps;
        c.PowerFactor = ui.PowerFactor;
        c.RefractionA = ui.RefractionA;
        c.RefractionB = ui.RefractionB;
        c.RefractionC = ui.RefractionC;
        c.RefractionD = ui.RefractionD;
        c.FPower = ui.FPower;
        c.GlowWeight = ui.GlowWeight;
        c.GlowBias = ui.GlowBias;
        c.GlowEdge0 = ui.GlowEdge0;
        c.GlowEdge1 = ui.GlowEdge1;

        string activePreset = (GlassPresetCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? GlassPresetCustom;
        if (activePreset == GlassPresetClear) c.Variant = 1;
        else if (activePreset == GlassPresetRegular || activePreset == GlassPresetFrosted || activePreset == GlassPresetDark || activePreset == GlassPresetUltraThin || activePreset == GlassPresetThin || activePreset == GlassPresetThick || activePreset == GlassPresetUltraThick) c.Variant = 0;
        else c.Variant = _customGlassSnapshot?.Variant ?? c.Variant;

        c.UseGpuRefraction = GpuRefractionCheck?.IsChecked ?? false;
        c.UseUltimateGpu = UltimateGpuCheck?.IsChecked ?? false;

        // Persist which preset is active and the user's custom slot. A built-in
        target.LiquidGlassPreset = activePreset;
        if (_customGlassSnapshot != null)
            target.LiquidGlassCustom = _customGlassSnapshot.Clone();
    }

    private void GlassPresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || _suppressGlassPresetChange) return;
        if (GlassPresetCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;

        Models.LiquidGlassConfig? preset = null;
        switch (item.Tag as string)
        {
            case GlassPresetFrosted: preset = FrostedGlassPreset(); break;
            case GlassPresetDark: preset = DarkGlassPreset(); break;
            case GlassPresetRegular: preset = RegularGlassPreset(); break;
            case GlassPresetUltraThin: preset = UltraThinGlassPreset(); break;
            case GlassPresetThin: preset = ThinGlassPreset(); break;
            case GlassPresetThick: preset = ThickGlassPreset(); break;
            case GlassPresetUltraThick: preset = UltraThickGlassPreset(); break;
            case GlassPresetClear: preset = ClearGlassPreset(); break;
            default:
                if (_customGlassSnapshot != null) preset = _customGlassSnapshot;
                break;
        }

        if (preset != null)
        {
            var currentFps = (int)Math.Round(GlassFpsSlider.Value);
            var currentGpu = GpuRefractionCheck.IsChecked ?? false;
            var currentUltimateGpu = UltimateGpuCheck?.IsChecked ?? false;
            _settings.LiquidGlass = preset.Clone();
            _settings.LiquidGlass.TargetFps = currentFps;
            _settings.LiquidGlass.UseGpuRefraction = currentGpu;
            _settings.LiquidGlass.UseUltimateGpu = currentUltimateGpu;
            ApplyGlassConfigToSliders(_settings.LiquidGlass);
        }

        PushLivePreview();
    }

    private void SkinCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateLiquidGlassAvailability(animate: true);

        if (_isLoadingSettings) return;
        string requestedStyle =
            (SkinCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "default";
        _settings.NotchStyle = string.Equals(requestedStyle, SkinLiquidGlass, StringComparison.OrdinalIgnoreCase)
            ? SkinLiquidGlass
            : "default";
        ApplyEffectiveGpuPreference();
        PushLivePreview();
    }

    // Populates the skin selector. Plain string items so the selection box renders
    private void PopulateSkinItems()
    {
        SkinCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.Get("settings.skin.default"), Tag = "default" });
        SkinCombo.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Content = Loc.Get("settings.skin.liquidglass"),
            Tag = SkinLiquidGlass,
            IsEnabled = true
        });
    }

    private void UpdateLiquidGlassAvailability(bool animate = false)
    {
        if (SkinCombo == null) return;

        bool glassSelected = SkinCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selected &&
                             (selected.Tag as string) == SkinLiquidGlass;

        AnimateCollapsibleRow(LiquidGlassConfigPanel, glassSelected, animate);
    }

    private void GlassConfigSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingSettings) return;

        string currentPreset = (GlassPresetCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? GlassPresetCustom;
        if (currentPreset != GlassPresetCustom && _settings.LiquidGlass != null)
        {
            var defaults = new Models.LiquidGlassConfig();
            _settings.LiquidGlass.PowerFactor = defaults.PowerFactor;
            _settings.LiquidGlass.RefractionA = defaults.RefractionA;
            _settings.LiquidGlass.RefractionB = defaults.RefractionB;
            _settings.LiquidGlass.RefractionC = defaults.RefractionC;
            _settings.LiquidGlass.RefractionD = defaults.RefractionD;
            _settings.LiquidGlass.FPower = defaults.FPower;
            _settings.LiquidGlass.GlowWeight = defaults.GlowWeight;
            _settings.LiquidGlass.GlowBias = defaults.GlowBias;
            _settings.LiquidGlass.GlowEdge0 = defaults.GlowEdge0;
            _settings.LiquidGlass.GlowEdge1 = defaults.GlowEdge1;
            _settings.LiquidGlass.Variant = defaults.Variant;
        }

        // A manual slider tweak means the values no longer match a named preset —
        _customGlassSnapshot = ReadGlassConfigFromSliders();
        SelectGlassPreset(GlassPresetCustom);

        PushLivePreview();
    }

    private void GlassConfigCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        PushLivePreview();
    }

    private void GpuRefractionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool gpuEnabled = GpuRefractionCheck?.IsChecked ?? false;
        if (_settings.LiquidGlass != null && GpuRefractionCheck != null)
        {
            _settings.LiquidGlass.UseGpuRefraction = gpuEnabled;
        }
        if (UltimateGpuCheck != null)
        {
            UltimateGpuCheck.IsEnabled = gpuEnabled;
            UltimateGpuCheck.Opacity = gpuEnabled ? 1.0 : 0.4;
        }
        ApplyEffectiveGpuPreference();
        PushLivePreview();
    }

    private void UltimateGpuCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings) return;
        bool enabled = UltimateGpuCheck?.IsChecked ?? false;
        if (enabled && IsLoaded)
        {
            bool confirmed = VNotch.Windows.ConfirmationDialog.Show(
                this,
                Loc.Get("settings.glass.ultimateGpu.confirm"),
                Loc.Get("settings.glass.ultimateGpu.title"),
                Loc.Get("dialog.confirm"),
                Loc.Get("dialog.cancel"),
                VNotch.Windows.ConfirmationDialog.DialogIcon.Warning,
                VNotch.Windows.ConfirmationDialog.DialogStyle.Normal);

            if (!confirmed)
            {
                bool prev = _isLoadingSettings;
                _isLoadingSettings = true;
                try
                {
                    if (UltimateGpuCheck != null)
                        UltimateGpuCheck.IsChecked = false;
                }
                finally
                {
                    _isLoadingSettings = prev;
                }
                return;
            }
        }

        if (_settings.LiquidGlass != null)
        {
            _settings.LiquidGlass.UseUltimateGpu = enabled;
        }
        if (UltimateGpuWarning != null)
        {
            UltimateGpuWarning.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplyEffectiveGpuPreference();
        PushLivePreview();
    }

    private void ApplyLiquidGlassLocalization()
    {
        if (SkinLabel == null) return;

        if (SkinHeader != null) SkinHeader.Text = Loc.Get("settings.skins");
        SkinLabel.Text = Loc.Get("settings.skin");
        if (SkinExperimentalBadge != null) SkinExperimentalBadge.Text = Loc.Get(LocKeyBadgeExperimental);
        SkinHint.Text = Loc.Get("settings.skin.hint");
        if (SkinWarningNote != null) SkinWarningNote.Text = Loc.Get("settings.skin.warning");

        int idx = SkinCombo.SelectedIndex;
        bool prev = _isLoadingSettings;
        _isLoadingSettings = true;
        SkinCombo.Items.Clear();
        PopulateSkinItems();
        SkinCombo.SelectedIndex = idx < 0 ? 0 : idx;
        UpdateLiquidGlassAvailability();
        _isLoadingSettings = prev;

        if (GlassPresetLabel != null) GlassPresetLabel.Text = Loc.Get("settings.glass.preset");
        if (GlassAdvancedWarning != null) GlassAdvancedWarning.Text = Loc.Get("settings.glass.advancedWarning");
        string presetTag = (GlassPresetCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? GlassPresetCustom;
        _suppressGlassPresetChange = true;
        GlassPresetCombo.Items.Clear();
        EnsureGlassPresetItems();
        SelectGlassPreset(presetTag);
        _suppressGlassPresetChange = false;

        GlassBlurSlider.Label = Loc.Get("settings.glass.blur");
        GlassRefractionSlider.Label = Loc.Get("settings.glass.refraction");
        GlassEdgeBendSlider.Label = Loc.Get("settings.glass.edgeBend");
        GlassChromSlider.Label = Loc.Get("settings.glass.chrom");
        GlassEdgeHighlightSlider.Label = Loc.Get("settings.glass.edgeHighlight");
        GlassTouchLightSlider.Label = Loc.Get("settings.glass.touchLight") ?? "Touch Light";
        GlassSpecularSlider.Label = Loc.Get("settings.glass.specular");
        GlassFresnelSlider.Label = Loc.Get("settings.glass.fresnel");
        GlassDistortionSlider.Label = Loc.Get("settings.glass.distortion");
        GlassGrainSlider.Label = Loc.Get("settings.glass.grain");
        GlassZRadiusSlider.Label = Loc.Get("settings.glass.zRadius");
        GlassOpacitySlider.Label = Loc.Get("settings.glass.opacity");
        GlassSaturationSlider.Label = Loc.Get("settings.glass.saturation");
        GlassBrightnessSlider.Label = Loc.Get("settings.glass.brightness");
        GlassShadowOpacitySlider.Label = Loc.Get("settings.glass.shadowOpacity");
        GlassShadowSpreadSlider.Label = Loc.Get("settings.glass.shadowSpread");
        GlassBevelModeSlider.Label = Loc.Get("settings.glass.bevelMode");
        GlassFpsSlider.Label = Loc.Get("settings.glass.targetFps");
    }

    #endregion
}
