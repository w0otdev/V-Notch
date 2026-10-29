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
    private sealed record ModelChoice(string Id, string Name, string Group, bool IsRecommended, string RecommendedLabel)
    {
        public override string ToString() => IsRecommended ? $"{Name} · {RecommendedLabel}" : Name;
    }

    private void LoadTranslationModelChoices()
    {
        string selected = TranslationModelCatalog.Normalize(_settings.TranslationModelId);
        PopulateTranslationModelChoices(selected);
        SelectTranslationStore(selected);
        RefreshTranslationModelDetails();
        if (!_translationHardwareStarted && !IsDeferringBackgroundInitialization)
        {
            _translationHardwareStarted = true;
            _ = DetectTranslationHardwareAsync();
        }
    }

    private async Task DetectTranslationHardwareAsync()
    {
        try { _translationHardware = await Task.Run(TranslationHardware.Detect, CancellationToken.None); }
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
            var choices = TranslationModelCatalog.Available.OrderByDescending(p => p.Category).ThenBy(p => p.MinimumRamGiB).Select(p => new ModelChoice(p.Id,
                p.DisplayName + " · " + (p.IsCustom ? Loc.Get("translation.customUnrated") : Loc.Get("translation.ratingCompact", p.TranslationRating) + " · " + Loc.Get("translation.tier." + p.Tier)),
                Loc.Get("translation.category." + p.Category), p.Id == recommended, Loc.Get("translation.recommended"))).ToArray();
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
        TranslationModelRating.Text = Loc.Get("translation.rating", model.TranslationRating);
        TranslationModelRatingHint.Text = Loc.Get("translation.ratingHint");
        TranslationCustomImportButton.Content = Loc.Get("translation.customImport");
        TranslationCustomHint.Text = Loc.Get("translation.customHint");
        TranslationCustomPromptLabel.Text = Loc.Get("translation.customPrompt");
        TranslationCustomGeneral.Content = Loc.Get("translation.category.general");
        TranslationHardwareHint.Text = _translationHardware is { RamGiB: > 0 } h
            ? Loc.Get("translation.hardware", h.RamGiB, h.VramGiB, h.LogicalProcessors)
            : Loc.Get("translation.hardwareUnknown");
        string fit = _translationHardware?.Fits(model) == false ? Loc.Get("translation.modelLowMemory") : Loc.Get("translation.modelEstimate");
        TranslationModelFitHint.Text = Loc.Get("translation.modelRequirements", model.MinimumRamGiB, model.GpuGiB) + " " + fit +
            (model.PromptKind == TranslationPromptKind.TranslateGemma ? " " + Loc.Get("translation.modelSourceHint") : "");
        TranslationModelDetailsButton.Visibility = model.IsCustom ? Visibility.Collapsed : Visibility.Visible;
        TranslationDownloadPrivacy.Visibility = model.IsCustom ? Visibility.Collapsed : Visibility.Visible;
        if (model.IsCustom)
        {
            TranslationModelRating.Text = Loc.Get("translation.customUnrated");
            TranslationModelRatingHint.Text = "";
            TranslationModelHint.Text = model.DisplayName + " · " + FormatTranslationBytes(_translationStore.TotalBytes);
            TranslationModelFitHint.Text = "";
        }
        if (TranslationModelCombo.ItemsSource != null) PopulateTranslationModelChoices(model.Id);
    }

    private void TranslationModelDetails_Click(object sender, RoutedEventArgs e) =>
        SafeLauncher.TryOpenUrl(string.Concat("https://", "huggingface.co/", _translationStore.Profile.Repository));

    private async void TranslationCustomImport_Click(object sender, RoutedEventArgs e)
    {
        if (_translationDownload != null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GGUF|*.gguf", Title = Loc.Get("translation.customImport"), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var kind = TranslationCustomPrompt.SelectedIndex switch
        { 1 => TranslationPromptKind.TranslateGemma, 2 => TranslationPromptKind.Hunyuan, _ => TranslationPromptKind.General };
        TranslationModelProfile? imported = null;
        await RunTranslationModelActionAsync(async ct =>
        {
            TranslationProgressDetail.Text = "";
            var progress = new Progress<double>(percent =>
            {
                if (_translationWindowClosed || ct.IsCancellationRequested) return;
                _translationWaiting = false;
                UpdateTranslationPulse();
                TranslationModelProgress.Value = percent;
                TranslationProgressPercent.Text = $"{percent:0}%";
            });
            imported = await Task.Run(() => CustomTranslationModels.Shared.ImportAsync(dialog.FileName, kind, ct, progress), ct);
        }, "translation.customImport");
        if (imported == null || _translationWindowClosed) return;
        SelectTranslationStore(imported.Id);
        PopulateTranslationModelChoices(imported.Id);
        RefreshTranslationModelDetails();
        RefreshTranslationModelStatus();
        PushLivePreview();
    }
}
