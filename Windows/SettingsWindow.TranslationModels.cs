using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VNotch.Services;
using VNotch.Services.Translation;

namespace VNotch;

public partial class SettingsWindow
{
    private TranslationHardware? _translationHardware;
    private bool _translationHardwareStarted, _translationModelLoading;
    private sealed record ModelChoice(string Id, string Name, string Group)
    {
        public override string ToString() => Name;
    }

    private void LoadTranslationModelChoices()
    {
        string selected = TranslationModelCatalog.Normalize(_settings.TranslationModelId);
        PopulateTranslationModelChoices(selected);
        SelectTranslationStore(selected);
        RefreshTranslationModelDetails();
        if (!_translationHardwareStarted)
        {
            _translationHardwareStarted = true;
            _ = DetectTranslationHardwareAsync();
        }
    }

    private async Task DetectTranslationHardwareAsync()
    {
        try { _translationHardware = await Task.Run(TranslationHardware.Detect); }
        catch { _translationHardware = new(0, 0, Environment.ProcessorCount, ""); }
        if (_translationWindowClosed || Dispatcher.HasShutdownStarted) return;
        PopulateTranslationModelChoices(_translationStore.Profile.Id);
        RefreshTranslationModelDetails();
    }

    private void PopulateTranslationModelChoices(string selected)
    {
        _translationModelLoading = true;
        try
        {
            string? recommended = _translationHardware?.Recommended()?.Id;
            var choices = TranslationModelCatalog.All.OrderByDescending(p => p.Category).ThenBy(p => p.MinimumRamGiB).Select(p => new ModelChoice(p.Id,
                p.DisplayName + " · " + Loc.Get("translation.tier." + p.Tier) +
                (p.Id == recommended ? " · " + Loc.Get("translation.recommended") : ""),
                Loc.Get("translation.category." + p.Category))).ToArray();
            var view = new ListCollectionView(choices);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelChoice.Group)));
            TranslationModelCombo.ItemsSource = view;
            TranslationModelCombo.SelectedValue = selected;
        }
        finally { _translationModelLoading = false; }
    }

    private void SelectTranslationStore(string id)
    {
        var next = TranslationModelStore.For(id);
        if (ReferenceEquals(next, _translationStore)) return;
        if (_translationInitialized) _translationStore.Changed -= TranslationModelChanged;
        _translationStore = next;
        _translationModelInstalled = false;
        if (_translationInitialized) _translationStore.Changed += TranslationModelChanged;
    }

    private void TranslationModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_translationModelLoading || _isLoadingSettings || TranslationModelCombo.SelectedValue is not string id) return;
        SelectTranslationStore(id);
        RefreshTranslationModelDetails();
        RefreshTranslationModelStatus();
        PushLivePreview();
    }

    private void RefreshTranslationModelDetails()
    {
        var model = _translationStore.Profile;
        TranslationModelTitle.Text = Loc.Get("translation.modelTitle");
        TranslationModelHint.Text = Loc.Get("translation.modelDetails", model.DisplayName,
            _translationStore.TotalBytes / 1_000_000_000d, model.License);
        TranslationModelDetailsButton.Content = Loc.Get("translation.modelSource");
        TranslationHardwareHint.Text = _translationHardware is { RamGiB: > 0 } h
            ? Loc.Get("translation.hardware", h.RamGiB, h.VramGiB, h.LogicalProcessors)
            : Loc.Get("translation.hardwareUnknown");
        string fit = _translationHardware?.Fits(model) == false ? Loc.Get("translation.modelLowMemory") : Loc.Get("translation.modelEstimate");
        TranslationModelFitHint.Text = Loc.Get("translation.modelRequirements", model.MinimumRamGiB, model.GpuGiB) + " " + fit +
            (model.PromptKind == TranslationPromptKind.TranslateGemma ? " " + Loc.Get("translation.modelSourceHint") : "");
        if (TranslationModelCombo.ItemsSource != null) PopulateTranslationModelChoices(model.Id);
    }

    private void TranslationModelDetails_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://huggingface.co/" + _translationStore.Profile.Repository) { UseShellExecute = true });
}
