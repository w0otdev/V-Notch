using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Translation;

namespace VNotch;

public partial class SettingsWindow
{
    internal event Action? TranslationPreviewRequested;
    private CancellationTokenSource? _translationDownload;
    private TranslationModelStore _translationStore = TranslationModelStore.Shared;
    private bool _translationInitialized, _translationWindowClosed;
    private bool _translationWaiting;
    private bool _translationModelInstalled;

    internal void NavigateToTranslation() => NavigateToSection("Translation");

    private void LoadTranslationSettings()
    {
        LoadTranslationModelChoices();
        LoadTranslationAdvanced(_settings);
        EnableTranslationCheck.IsChecked = _settings.EnableLiveTranslation;
        AutoTranslationCheck.IsChecked = _settings.AutoLiveTranslation;
        TranslationIgnoreFullscreenCheck.IsChecked = _settings.TranslationIgnoreFullscreen;
        TranslationExcludedAppsBox.Text = _settings.TranslationExcludedApps;
        FormatTranslationCheck.IsChecked = _settings.FormatTranslationText;
        HumanizerTranslationCheck.IsChecked = _settings.EnableTranslationHumanizer;
        CurrencyConversionCheck.IsChecked = _settings.EnableCurrencyConversion;
        LoadTranslationCurrencies(_settings.TranslationTargetCurrency);
        TranslationSourceCombo.ItemsSource = new[] { new TranslationLanguages.Language("auto", Loc.Get("translation.autoDetect")) }.Concat(TranslationLanguages.All);
        TranslationTargetCombo.ItemsSource = TranslationLanguages.All;
        TranslationSourceCombo.SelectedValue = TranslationLanguages.Normalize(_settings.TranslationSourceLanguage, true);
        TranslationTargetCombo.SelectedValue = TranslationLanguages.Normalize(_settings.TranslationTargetLanguage);
        if (!_translationInitialized)
        {
            _translationInitialized = true;
            _translationStore.Changed += TranslationModelChanged;
            AnimationConfig.ReduceMotionChanged += TranslationMotionChanged;
            Closed += (_, _) =>
            {
                _translationWindowClosed = true;
                _translationDownload?.Cancel();
                _translationStore.Changed -= TranslationModelChanged;
                AnimationConfig.ReduceMotionChanged -= TranslationMotionChanged;
                TranslationPulseOffset.BeginAnimation(TranslateTransform.XProperty, null);
            };
        }
        RefreshTranslationModelStatus();
    }

    private void ReadTranslationSettings(NotchSettings snapshot)
    {
        ReadTranslationAdvanced(snapshot);
        snapshot.TranslationModelId = TranslationModelCatalog.Normalize(TranslationModelCombo.SelectedValue as string);
        snapshot.EnableLiveTranslation = EnableTranslationCheck.IsChecked == true;
        snapshot.AutoLiveTranslation = AutoTranslationCheck.IsChecked == true;
        snapshot.TranslationIgnoreFullscreen = TranslationIgnoreFullscreenCheck.IsChecked == true;
        snapshot.TranslationExcludedApps = TranslationExcludedAppsBox.Text.Trim();
        snapshot.FormatTranslationText = FormatTranslationCheck.IsChecked == true;
        snapshot.EnableTranslationHumanizer = HumanizerTranslationCheck.IsChecked == true;
        snapshot.EnableCurrencyConversion = CurrencyConversionCheck.IsChecked == true;
        snapshot.TranslationTargetCurrency = TranslationCurrencies.Normalize(TranslationCurrencyCombo.SelectedValue as string);
        snapshot.TranslationSourceLanguage = TranslationLanguages.Normalize(TranslationSourceCombo.SelectedValue as string, true);
        snapshot.TranslationTargetLanguage = TranslationLanguages.Normalize(TranslationTargetCombo.SelectedValue as string);
    }

    private void TranslationSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoadingSettings) { PushLivePreview(); RefreshTranslationReadyUsage(); TranslationPreviewButton.IsEnabled = _translationStore.IsInstalled || CurrencyConversionCheck.IsChecked == true; }
    }
    private void TranslationLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingSettings && TranslationSourceCombo.SelectedValue != null && TranslationTargetCombo.SelectedValue != null) PushLivePreview();
    }

    private void TranslationExcludedApps_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_isLoadingSettings && _translationInitialized) PushLivePreview();
    }

    private void LocalizeTranslationSettings()
    {
        LocalizeTranslationAdvanced();
        NavTranslationText.Text = Loc.Get("translation.title");
        EnableTranslationLabel.Text = Loc.Get("translation.enable");
        TranslationExperimentalBadge.Text = Loc.Get("settings.badge.experimental");
        TranslationEnableHint.Text = Loc.Get("translation.enableHint");
        AutoTranslationCheck.Content = Loc.Get("translation.auto");
        TranslationIgnoreFullscreenCheck.Content = Loc.Get("translation.ignoreFullscreen");
        TranslationExcludedAppsLabel.Text = Loc.Get("translation.excludedApps");
        TranslationExcludedAppsHint.Text = Loc.Get("translation.excludedAppsHint");
        TranslationAutoHint.Text = Loc.Get("translation.autoHint");
        FormatTranslationCheck.Content = Loc.Get("translation.formatText");
        TranslationFormatHint.Text = Loc.Get("translation.formatTextHint");
        HumanizerTranslationCheck.Content = Loc.Get("translation.humanizer");
        TranslationHumanizerHint.Text = Loc.Get("translation.humanizerHint");
        TranslationSourceLabel.Text = Loc.Get("translation.source");
        TranslationTargetLabel.Text = Loc.Get("translation.target");
        CurrencyConversionCheck.Content = Loc.Get("translation.currencyEnable");
        TranslationCurrencyLabel.Text = Loc.Get("translation.currencyTarget");
        TranslationCurrencyHint.Text = Loc.Get("translation.currencyHint");
        LoadTranslationCurrencies(TranslationCurrencyCombo.SelectedValue as string ?? _settings.TranslationTargetCurrency);
        RefreshTranslationModelDetails();
        TranslationDownloadPrivacy.Text = Loc.Get("translation.downloadPrivacy");
        TranslationDownloadButton.Content = Loc.Get(_translationDownload != null ? "translation.cancel" : "translation.download");
        TranslationImportButton.Content = Loc.Get("translation.import");
        TranslationRemoveButton.Content = Loc.Get("translation.remove");
        TranslationPreviewButton.Content = Loc.Get("translation.fromClipboard");
        TranslationQualityHint.Text = Loc.Get("translation.qualityHint");
        TranslationReadyTitle.Text = Loc.Get("translation.installComplete");
        TranslationUsageTitle.Text = Loc.Get("translation.usageTitle");
        TranslationUsageSetup.Text = Loc.Get("translation.usageSetup");
        TranslationUsageSelection.Text = Loc.Get("translation.usageSelection");
        TranslationUsageActions.Text = Loc.Get("translation.usageActions");
        RefreshTranslationReadyUsage();
        if (_translationDownload == null) RefreshTranslationModelStatus();
    }

    private void TranslationModelChanged()
    {
        // This operation refreshes its own outcome. A queued store notification
        // must not overwrite a connection error with the generic missing-model label.
        if (_translationDownload != null) return;
        if (!_translationWindowClosed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
        { if (!_translationWindowClosed && _translationDownload == null) RefreshTranslationModelStatus(); });
    }

    private void RefreshTranslationModelStatus() => PresentTranslationModelAvailability(_translationStore.IsInstalled);

    internal void PresentTranslationModelAvailability(bool installed)
    {
        bool justInstalled = installed && !_translationModelInstalled;
        _translationModelInstalled = installed;
        TranslationModelStatus.Text = Loc.Get(installed ? "translation.modelReady" : "translation.modelMissing");
        TranslationModelStatus.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        TranslationReadyCard.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        TranslationDownloadButton.Visibility = installed || _translationStore.Profile.IsCustom ? Visibility.Collapsed : Visibility.Visible;
        TranslationDownloadButton.IsEnabled = !installed && !_translationStore.Profile.IsCustom;
        TranslationImportButton.IsEnabled = !_translationStore.Profile.IsCustom && _translationDownload == null;
        TranslationRemoveButton.IsEnabled = installed;
        TranslationPreviewButton.IsEnabled = installed || CurrencyConversionCheck.IsChecked == true;
        RefreshTranslationReadyUsage();
        if (justInstalled && !_isLoadingSettings && !AnimationConfig.ReduceMotion)
        {
            TranslationReadyCard.BeginAnimation(OpacityProperty, TranslationAnimation(0, 1, 240));
            TranslationReadyEntrance.BeginAnimation(TranslateTransform.YProperty, TranslationAnimation(6, 0, 240));
        }
    }

    private void RefreshTranslationReadyUsage()
    {
        if (TranslationReadyHint == null) return;
        bool enabled = EnableTranslationCheck.IsChecked == true;
        string readyKey = AutoTranslationCheck.IsChecked == true ? "translation.readyAutoHint" : "translation.readyManualHint";
        TranslationReadyHint.Text = Loc.Get(!enabled ? "translation.readyEnableHint" : readyKey);
    }

    private async void TranslationDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_translationDownload != null) { await _translationDownload.CancelAsync(); return; }
        if (!NetworkPrivacy.Current.IsAllowed(NetworkFeature.TranslationModels))
        { TranslationModelStatus.Text = Loc.Get("translation.downloadBlocked"); return; }
        await RunTranslationModelActionAsync(ct =>
        {
            var operation = _translationDownload;
            var progress = new Progress<TranslationDownloadProgress>(state =>
            {
                if (!_translationWindowClosed && ReferenceEquals(_translationDownload, operation) && !ct.IsCancellationRequested)
                    ShowTranslationDownloadProgress(state);
            });
            return _translationStore.DownloadAsync(null, ct, progress);
        }, "translation.connecting");
    }

    private async void TranslationImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = $"{_translationStore.Profile.DisplayName}|{_translationStore.Profile.Assets[0].Name}", Title = Loc.Get("translation.import") };
        if (dialog.ShowDialog(this) == true)
            await RunTranslationModelActionAsync(ct => _translationStore.ImportAsync(System.IO.Path.GetDirectoryName(dialog.FileName)!, ct));
    }

    private async void TranslationRemove_Click(object sender, RoutedEventArgs e)
    {
        bool custom = _translationStore.Profile.IsCustom, removed = false;
        await RunTranslationModelActionAsync(async ct => { await _translationStore.RemoveAsync(ct); removed = true; });
        if (!custom || !removed || _translationWindowClosed) return;
        SelectTranslationStore(TranslationModelProfile.QwenInstruct.Id);
        PopulateTranslationModelChoices(_translationStore.Profile.Id);
        RefreshTranslationModelDetails();
        RefreshTranslationModelStatus();
        PushLivePreview();
    }

    internal async Task RunTranslationModelActionAsync(Func<CancellationToken, Task> action, string initialStatus = "translation.verifying")
    {
        if (_translationDownload != null) return;
        using var cancellation = new CancellationTokenSource();
        _translationDownload = cancellation;
        TranslationModelCombo.IsEnabled = false;
        TranslationCustomImportButton.IsEnabled = TranslationCustomPrompt.IsEnabled = false;
        TranslationDownloadButton.IsEnabled = true;
        TranslationDownloadButton.Content = Loc.Get("translation.cancel");
        TranslationImportButton.IsEnabled = TranslationRemoveButton.IsEnabled = TranslationPreviewButton.IsEnabled = false;
        TranslationModelProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        TranslationModelProgress.Value = 0;
        TranslationReadyCard.Visibility = Visibility.Collapsed;
        TranslationModelStatus.Visibility = Visibility.Collapsed;
        TranslationDownloadButton.Visibility = Visibility.Visible;
        TranslationModelStatus.Text = Loc.Get(initialStatus);
        TranslationProgressCaption.Text = Loc.Get(initialStatus);
        TranslationProgressPercent.Text = "";
        TranslationProgressDetail.Text = Loc.Get("translation.downloadSize", FormatTranslationBytes(0), FormatTranslationBytes(_translationStore.TotalBytes));
        _translationWaiting = true;
        TranslationProgressCard.Visibility = Visibility.Visible;
        if (!AnimationConfig.ReduceMotion)
        {
            TranslationProgressCard.BeginAnimation(OpacityProperty, TranslationAnimation(0, 1, 200));
            TranslationProgressEntrance.BeginAnimation(TranslateTransform.YProperty, TranslationAnimation(6, 0, 200));
        }
        UpdateTranslationPulse();
        string? error = null;
        object[] errorArguments = [];
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { error = "translation.downloadCancelled"; }
        catch (TranslationException ex) { error = ex.MessageKey; }
        catch (HttpRequestException ex)
        {
            error = ex.StatusCode.HasValue ? "translation.downloadHttpError" : "translation.downloadConnectionError";
            if (ex.StatusCode.HasValue) errorArguments = [(int)ex.StatusCode.Value];
            RuntimeLog.Warn("TRANSLATION-MODEL", $"HTTP download failure; status={(ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : 0)}.");
        }
        catch (UnauthorizedAccessException)
        { error = "translation.modelAccessDenied"; }
        catch (IOException)
        { error = "translation.modelIoFailed"; }
        catch (Exception ex)
        {
            error = "translation.installUnexpected";
            // Do not log exception messages: HTTP exceptions can include signed URLs.
            RuntimeLog.Warn("TRANSLATION-MODEL", $"Unexpected installation failure: {ex.GetType().Name}.");
        }
        finally
        {
            _translationDownload = null;
            TranslationModelCombo.IsEnabled = true;
            TranslationCustomImportButton.IsEnabled = TranslationCustomPrompt.IsEnabled = true;
            if (!_translationWindowClosed)
            {
                _translationWaiting = false;
                UpdateTranslationPulse();
                TranslationProgressCard.Visibility = Visibility.Collapsed;
                TranslationImportButton.IsEnabled = true;
                TranslationDownloadButton.Content = Loc.Get("translation.download");
                RefreshTranslationModelStatus();
                if (error != null)
                {
                    TranslationModelStatus.Text = Loc.Get(error, errorArguments);
                    TranslationModelStatus.Visibility = Visibility.Visible;
                }
            }
        }
    }

    internal void ShowTranslationDownloadProgress(TranslationDownloadProgress state)
    {
        _translationWaiting = state.Stage is TranslationDownloadStage.Waiting or TranslationDownloadStage.Connecting or TranslationDownloadStage.Verifying;
        string key = state.Stage switch
        {
            TranslationDownloadStage.Waiting => "translation.downloadWaiting",
            TranslationDownloadStage.Connecting => "translation.connecting",
            TranslationDownloadStage.Verifying => "translation.verifying",
            TranslationDownloadStage.Completed => "translation.modelReady",
            _ => "translation.downloading"
        };
        string caption = state.Stage == TranslationDownloadStage.Downloading ? Loc.Get(key, (int)state.Percent) : Loc.Get(key);
        TranslationModelStatus.Text = caption;
        TranslationProgressCaption.Text = state.Stage == TranslationDownloadStage.Downloading
            ? Loc.Get("translation.downloadingTitle") : caption;
        TranslationProgressPercent.Text = $"{(int)state.Percent}%";
        string size = Loc.Get("translation.downloadSize", FormatTranslationBytes(state.DownloadedBytes), FormatTranslationBytes(state.TotalBytes));
        TranslationProgressDetail.Text = state.BytesPerSecond > 0
            ? size + " · " + Loc.Get("translation.downloadSpeed", FormatTranslationBytes((long)state.BytesPerSecond)) : size;
        double from = TranslationModelProgress.Value;
        TranslationModelProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        TranslationModelProgress.Value = state.Percent;
        if (!AnimationConfig.ReduceMotion && TranslationProgressCard.IsVisible)
            TranslationModelProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, TranslationAnimation(from, state.Percent, 220));
        UpdateTranslationPulse();
    }

    private static string FormatTranslationBytes(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000d:0.00} GB" : $"{bytes / 1_000_000d:0.0} MB";

    private static DoubleAnimation TranslationAnimation(double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        { EasingFunction = AnimationPrimitives._easeQuadOut };
        Timeline.SetDesiredFrameRate(animation, Math.Min(60, AnimationConfig.TargetFps));
        return animation;
    }

    private void TranslationMotionChanged()
    {
        if (!_translationWindowClosed && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(UpdateTranslationPulse);
    }

    private void TranslationProgress_VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTranslationPulse();
    private void TranslationProgress_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTranslationPulse();

    private void UpdateTranslationPulse()
    {
        if (TranslationProgressPulse == null) return;
        bool animate = _translationWaiting && TranslationProgressCard.IsVisible && !AnimationConfig.ReduceMotion;
        TranslationPulseOffset.BeginAnimation(TranslateTransform.XProperty, null);
        TranslationProgressPulse.Visibility = animate ? Visibility.Visible : Visibility.Collapsed;
        if (animate)
        {
            var sweep = new DoubleAnimation(-90, Math.Max(90, TranslationProgressTrack.ActualWidth), TimeSpan.FromMilliseconds(1200))
            { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(sweep, Math.Min(60, AnimationConfig.TargetFps));
            TranslationPulseOffset.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
    }

    private void TranslationPreview_Click(object sender, RoutedEventArgs e) => TranslationPreviewRequested?.Invoke();

    private void LoadTranslationCurrencies(string? selected)
    {
        bool loading = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            TranslationCurrencyCombo.ItemsSource = new[] { new TranslationLanguages.Language("auto", Loc.Get("translation.currencyAuto")) }
                .Concat(TranslationCurrencies.Codes.Select(c => new TranslationLanguages.Language(c, c))).ToArray();
            TranslationCurrencyCombo.SelectedValue = TranslationCurrencies.Normalize(selected);
        }
        finally { _isLoadingSettings = loading; }
    }

    private void TranslationCurrency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingSettings && TranslationCurrencyCombo.SelectedValue != null) PushLivePreview();
    }
}
