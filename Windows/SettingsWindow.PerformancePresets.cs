using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private static readonly SolidColorBrush PerfDividerBrush = CreateSettingsBrush(Color.FromArgb(20, 255, 255, 255));

    private readonly PerformancePresetSession _perfPresetSession = new();
    private bool _recommendedPerfPresetStarted;
    private bool _isApplyingPerformancePreset;
    private string _processStartupPriority = ProcessPriorityPolicy.Normal;
    private int _processStartupEffectiveGpu;

    internal string? PerformanceBackupFilePathOverride
    {
        get => _perfPresetSession.BackupFilePathOverride;
        set => _perfPresetSession.BackupFilePathOverride = value;
    }

    internal bool HasPerformanceUserBackup => _perfPresetSession.HasUserBackup;

    internal int ActivePerformancePresetSelection => _perfPresetSession.ResolveActiveSelection(_settings);

    private void CaptureProcessStartupPerformanceBaseline(NotchSettings startupSettings)
    {
        _processStartupPriority = ProcessPriorityPolicy.Normalize(startupSettings.ProcessPriority);
        _processStartupEffectiveGpu = GetEffectiveGpuPreference(startupSettings);
    }

    private void LoadPerformancePresetsUi()
    {
        if (!IsDeferringBackgroundInitialization)
        {
            _perfPresetSession.EnsureDiskBackupLoaded(_settingsAppService, _settings);
            StartPerformanceHardwareRecommendationIfNeeded();
        }

        RebuildPerformancePresetRows();
    }

    private void ApplyPerformancePresetsLocalization()
    {
        if (PerformancePresetsLabel != null)
            PerformancePresetsLabel.Text = Loc.Get("settings.performance.presets");
        if (PerformancePresetsHint != null)
            PerformancePresetsHint.Text = Loc.Get("settings.performance.presets.hint");

        RebuildPerformancePresetRows();
    }

    internal void StartPerformanceHardwareRecommendationIfNeeded()
    {
        if (_recommendedPerfPresetStarted || IsDeferringBackgroundInitialization)
            return;

        _recommendedPerfPresetStarted = true;
        _ = DetectRecommendedPerformancePresetAsync();
    }

    private async Task DetectRecommendedPerformancePresetAsync()
    {
        int recommended;
        try
        {
            recommended = await Task.Run(PerformancePresetCatalog.QueryRecommendedHardwareTier).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        if (_isClosing || Dispatcher.HasShutdownStarted || !PerformancePresetCatalog.IsValidPresetId(recommended))
            return;

        if (_perfPresetSession.RecommendedPresetId == recommended)
            return;

        _perfPresetSession.RecommendedPresetId = recommended;
        RebuildPerformancePresetRows();
    }

    internal void RefreshPerformancePresetSelectionUi(NotchSettings snapshot)
    {
        if (_isLoadingSettings || _isApplyingPerformancePreset || PerformancePresetListPanel == null)
            return;

        _perfPresetSession.NotifyExternalSettingsChanged(snapshot);
        RebuildPerformancePresetRows(snapshot);
    }

    private void RebuildPerformancePresetRows(NotchSettings? explicitSnapshot = null)
    {
        if (PerformancePresetListPanel == null)
            return;

        if (!IsDeferringBackgroundInitialization)
        {
            _perfPresetSession.EnsureDiskBackupLoaded(_settingsAppService, _settings);
        }

        NotchSettings snapshot = explicitSnapshot ?? _settings;
        int activeSelection = _perfPresetSession.ResolveActiveSelection(snapshot);

        PerformancePresetListPanel.Children.Clear();

        if (_perfPresetSession.HasUserBackup)
        {
            PerformancePresetListPanel.Children.Add(BuildUserBackupRow(activeSelection == VNotchNative.VN_PERF_SELECTION_BACKUP));
            PerformancePresetListPanel.Children.Add(new Border
            {
                Height = 1,
                Background = PerfDividerBrush,
                Margin = new Thickness(4, 6, 4, 6),
            });
        }

        foreach (PerformancePresetDescriptor preset in PerformancePresetCatalog.All)
        {
            PerformancePresetListPanel.Children.Add(BuildHardwarePresetRow(preset, activeSelection == preset.Id));
        }
    }

    private Button BuildUserBackupRow(bool isActive)
    {
        DateTimeOffset savedAt = _perfPresetSession.BackupSavedAt;
        string timeText = savedAt == default
            ? DateTimeOffset.Now.ToString("HH:mm")
            : savedAt.ToLocalTime().ToString("HH:mm");

        string statusLabel = isActive
            ? Loc.Get("settings.performance.preset.active")
            : Loc.Get("settings.performance.preset.rollback");

        return CreatePerformancePresetRowButton(
            selectionId: VNotchNative.VN_PERF_SELECTION_BACKUP,
            title: Loc.Get("settings.performance.preset.backup"),
            description: Loc.Get("settings.performance.preset.backup.desc", timeText),
            statusText: statusLabel,
            isActive: isActive);
    }

    private Button BuildHardwarePresetRow(PerformancePresetDescriptor preset, bool isActive)
    {
        string baseTitle = Loc.Get(preset.TitleLocKey);
        string title = preset.Id == _perfPresetSession.RecommendedPresetId
            ? $"{baseTitle} · {Loc.Get("settings.performance.preset.recommended")}"
            : baseTitle;

        return CreatePerformancePresetRowButton(
            selectionId: preset.Id,
            title: title,
            description: Loc.Get(preset.DescriptionLocKey),
            statusText: isActive ? Loc.Get("settings.performance.preset.active") : string.Empty,
            isActive: isActive);
    }

    private Button CreatePerformancePresetRowButton(
        int selectionId,
        string title,
        string description,
        string statusText,
        bool isActive)
    {
        var button = new Button
        {
            Tag = selectionId,
            Uid = isActive ? "Active" : "Idle",
            Style = (Style)FindResource("PerformancePresetRowButtonStyle"),
        };
        AutomationProperties.SetName(button, title);

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var accentIndicator = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 2, 12, 2),
            Opacity = isActive ? 1.0 : 0.0,
        };
        accentIndicator.SetResourceReference(Border.BackgroundProperty, "AccentBrushStrong");
        Grid.SetColumn(accentIndicator, 0);
        rowGrid.Children.Add(accentIndicator);

        var copyStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copyStack.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SettingTitleText"),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
        });
        copyStack.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("SettingHintText"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(copyStack, 1);
        rowGrid.Children.Add(copyStack);

        if (!string.IsNullOrEmpty(statusText))
        {
            var statusBlock = new TextBlock
            {
                Text = statusText,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            statusBlock.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "AccentBrushStrong" : "SoftText");
            Grid.SetColumn(statusBlock, 2);
            rowGrid.Children.Add(statusBlock);
        }

        button.Content = rowGrid;
        button.Click += PerformancePresetRow_Click;
        return button;
    }

    private async void PerformancePresetRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int selectionId })
            return;

        await SelectPerformancePresetItemAsync(selectionId, _translationDownload?.Token ?? CancellationToken.None);
    }

    internal Task<bool> SelectPerformancePresetItemAsync(int selectionId, CancellationToken ct = default)
    {
        if (selectionId == VNotchNative.VN_PERF_SELECTION_BACKUP)
            return RollbackPerformanceUserBackupAsync(ct);

        return ApplyHardwarePerformancePresetAsync(selectionId, ct);
    }

    private async Task<bool> ApplyHardwarePerformancePresetAsync(int presetId, CancellationToken ct)
    {
        if (!CanMutatePerformancePresetState())
            return false;

        _isApplyingPerformancePreset = true;
        try
        {
            _livePreviewDebounce?.Stop();
            NotchSettings currentSnapshot = ReadSettingsFromUi();

            if (!_perfPresetSession.TryPreparePresetSelection(
                    presetId,
                    _settingsAppService,
                    currentSnapshot,
                    out NotchSettings nextSnapshot,
                    out bool capturedBackupNow))
            {
                return false;
            }

            await ApplyImportedSettingsAsync(nextSnapshot, ct);
            _perfPresetSession.CommitPresetApplied(_settings);

            FinalizePerformancePresetTransition(
                capturedBackupNow
                    ? "settings.performance.preset.statusBackedUp"
                    : "settings.performance.preset.statusApplied");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PERF-PRESET", ex, "Failed to apply performance preset");
            return false;
        }
        finally
        {
            _isApplyingPerformancePreset = false;
        }
    }

    internal async Task<bool> RollbackPerformanceUserBackupAsync(CancellationToken ct = default)
    {
        if (!CanMutatePerformancePresetState())
            return false;

        _isApplyingPerformancePreset = true;
        try
        {
            _livePreviewDebounce?.Stop();
            NotchSettings currentSnapshot = ReadSettingsFromUi();

            if (!_perfPresetSession.TryPrepareRollback(_settingsAppService, currentSnapshot, out NotchSettings rollbackTarget))
                return false;

            await ApplyImportedSettingsAsync(rollbackTarget, ct);
            _perfPresetSession.CommitRollbackApplied(_settings);

            FinalizePerformancePresetTransition("settings.performance.preset.statusRestored");
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("PERF-PRESET", ex, "Failed to restore performance backup");
            return false;
        }
        finally
        {
            _isApplyingPerformancePreset = false;
        }
    }

    private bool CanMutatePerformancePresetState() =>
        !_isLoadingSettings && !_isSaving && !_isClosing && !_isApplyingPerformancePreset;

    private void FinalizePerformancePresetTransition(string statusLocKey)
    {
        ApplyImmediatePerformanceRuntimePolicies(_settings);
        RebuildPerformancePresetRows(_settings);
        ShowPerformancePresetStatus(Loc.Get(statusLocKey));
    }

    private void ApplyImmediatePerformanceRuntimePolicies(NotchSettings snapshot)
    {
        WindowAnimationPolicy.SetShadowEnabled(snapshot.EnableShadow);
        ProcessPriorityPolicy.Apply(snapshot.ProcessPriority);

        int effectiveGpu = GetEffectiveGpuPreference(snapshot);
        ApplyGpuPreference(effectiveGpu);

        bool priorityChangedSinceStartup = !string.Equals(
            ProcessPriorityPolicy.Normalize(snapshot.ProcessPriority),
            _processStartupPriority,
            StringComparison.OrdinalIgnoreCase);
        bool gpuChangedSinceStartup = effectiveGpu != _processStartupEffectiveGpu;

        if (priorityChangedSinceStartup || gpuChangedSinceStartup)
        {
            ShowRestartBanner();
            return;
        }

        HideRestartBanner();
    }

    private void ShowPerformancePresetStatus(string message)
    {
        if (PerformancePresetStatusText == null)
            return;

        if (PerformancePresetStatusText.Tag is UiScheduledTimer existingTimer)
        {
            existingTimer.Stop();
            existingTimer.Dispose();
            PerformancePresetStatusText.Tag = null;
        }

        PerformancePresetStatusText.Text = message;
        PerformancePresetStatusText.Foreground = BackupSuccessBrush;
        PerformancePresetStatusText.Visibility = Visibility.Visible;

        var timer = new UiScheduledTimer { Interval = TimeSpan.FromSeconds(3.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (ReferenceEquals(PerformancePresetStatusText.Tag, timer))
            {
                PerformancePresetStatusText.Tag = null;
            }
            PerformancePresetStatusText.Visibility = Visibility.Collapsed;
        };
        PerformancePresetStatusText.Tag = timer;
        timer.Start();
    }
}
