using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private static readonly SolidColorBrush PerfDividerBrush = CreateSettingsBrush(Color.FromArgb(20, 255, 255, 255));

    private readonly PerformancePresetSession _perfPresetSession = new();
    private readonly List<PerfPresetRowHandle> _perfPresetRows = new();
    private bool _perfPresetHasBackupRow;
    private bool _recommendedPerfPresetStarted;
    private bool _isApplyingPerformancePreset;
    private string _processStartupPriority = ProcessPriorityPolicy.Normal;
    private int _processStartupEffectiveGpu;

    private sealed class PerfPresetRowHandle
    {
        public required int SelectionId { get; init; }
        public required bool IsBackupRow { get; init; }
        public required Button Button { get; init; }
        public required Border AccentIndicator { get; init; }
        public required ScaleTransform AccentScale { get; init; }
        public required TextBlock TitleBlock { get; init; }
        public required TextBlock DescriptionBlock { get; init; }
        public required TextBlock ActiveStatusBlock { get; init; }
        public required TranslateTransform ActiveStatusTranslate { get; init; }
        public TextBlock? RollbackStatusBlock { get; init; }
        public TranslateTransform? RollbackStatusTranslate { get; init; }
        public bool IsActive { get; set; }
    }

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

        RebuildPerformancePresetRows(forceRebuild: true);
    }

    private void PerformancePresetListPanel_Loaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            SyncPerformancePresetSelectionIndicator(_perfPresetSession.ResolveActiveSelection(_settings), animate: false);
        });
    }

    private void PerformancePresetListPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                SyncPerformancePresetSelectionIndicator(_perfPresetSession.ResolveActiveSelection(_settings), animate: false);
            });
        }
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
        RebuildPerformancePresetRows(animateSelection: false);
    }

    internal void RefreshPerformancePresetSelectionUi(NotchSettings snapshot)
    {
        if (_isLoadingSettings || _isApplyingPerformancePreset || PerformancePresetListPanel == null)
            return;

        _perfPresetSession.NotifyExternalSettingsChanged(snapshot);
        RebuildPerformancePresetRows(snapshot, animateSelection: IsLoaded && PerformancePresetListPanel.IsVisible);
    }

    private void RebuildPerformancePresetRows(
        NotchSettings? explicitSnapshot = null,
        bool animateSelection = false,
        bool forceRebuild = false)
    {
        if (PerformancePresetListPanel == null)
            return;

        if (!IsDeferringBackgroundInitialization)
        {
            _perfPresetSession.EnsureDiskBackupLoaded(_settingsAppService, _settings);
        }

        NotchSettings snapshot = explicitSnapshot ?? _settings;
        int activeSelection = _perfPresetSession.ResolveActiveSelection(snapshot);
        bool hasBackup = _perfPresetSession.HasUserBackup;
        bool shouldAnimate = animateSelection && IsLoaded && PerformancePresetListPanel.IsVisible &&
                             !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;

        if (!forceRebuild && _perfPresetRows.Count > 0 && hasBackup == _perfPresetHasBackupRow)
        {
            UpdateAllPerformancePresetRowsInPlace(activeSelection, shouldAnimate);
            SyncPerformancePresetSelectionIndicator(activeSelection, shouldAnimate);
            return;
        }

        if (!forceRebuild && _perfPresetRows.Count > 0 && !_perfPresetHasBackupRow && hasBackup)
        {
            var backupHandle = BuildUserBackupRowHandle(activeSelection == VNotchNative.VN_PERF_SELECTION_BACKUP);
            var divider = new Border
            {
                Height = 1,
                Background = PerfDividerBrush,
                Margin = new Thickness(4, 6, 4, 6),
            };

            PerformancePresetListPanel.Children.Insert(0, backupHandle.Button);
            PerformancePresetListPanel.Children.Insert(1, divider);
            _perfPresetRows.Insert(0, backupHandle);
            _perfPresetHasBackupRow = true;

            if (shouldAnimate)
            {
                AnimateInsertedBackupRow(backupHandle.Button);
            }

            UpdateAllPerformancePresetRowsInPlace(activeSelection, shouldAnimate);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                SyncPerformancePresetSelectionIndicator(activeSelection, shouldAnimate);
            });
            return;
        }

        PerformancePresetListPanel.Children.Clear();
        _perfPresetRows.Clear();
        _perfPresetHasBackupRow = hasBackup;

        if (hasBackup)
        {
            var backupHandle = BuildUserBackupRowHandle(activeSelection == VNotchNative.VN_PERF_SELECTION_BACKUP);
            PerformancePresetListPanel.Children.Add(backupHandle.Button);
            PerformancePresetListPanel.Children.Add(new Border
            {
                Height = 1,
                Background = PerfDividerBrush,
                Margin = new Thickness(4, 6, 4, 6),
            });
            _perfPresetRows.Add(backupHandle);
        }

        foreach (PerformancePresetDescriptor preset in PerformancePresetCatalog.All)
        {
            var handle = BuildHardwarePresetRowHandle(preset, activeSelection == preset.Id);
            PerformancePresetListPanel.Children.Add(handle.Button);
            _perfPresetRows.Add(handle);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            SyncPerformancePresetSelectionIndicator(activeSelection, animate: false);
        });
    }

    private void UpdateAllPerformancePresetRowsInPlace(int activeSelection, bool animate)
    {
        for (int i = 0; i < _perfPresetRows.Count; i++)
        {
            PerfPresetRowHandle row = _perfPresetRows[i];
            if (row.IsBackupRow)
            {
                string timeText = ResolveBackupTimeText();
                ApplyRowContentAndSelection(
                    row,
                    Loc.Get("settings.performance.preset.backup"),
                    Loc.Get("settings.performance.preset.backup.desc", timeText),
                    activeSelection == VNotchNative.VN_PERF_SELECTION_BACKUP,
                    animate);
                continue;
            }

            if (FindDescriptorById(row.SelectionId) is not { } descriptor)
                continue;

            ApplyRowContentAndSelection(
                row,
                FormatHardwarePresetTitle(descriptor),
                Loc.Get(descriptor.DescriptionLocKey),
                activeSelection == descriptor.Id,
                animate);
        }
    }

    private static PerformancePresetDescriptor? FindDescriptorById(int presetId)
    {
        foreach (PerformancePresetDescriptor preset in PerformancePresetCatalog.All)
        {
            if (preset.Id == presetId)
                return preset;
        }
        return null;
    }

    private string ResolveBackupTimeText()
    {
        DateTimeOffset savedAt = _perfPresetSession.BackupSavedAt;
        return savedAt == default
            ? DateTimeOffset.Now.ToString("HH:mm")
            : savedAt.ToLocalTime().ToString("HH:mm");
    }

    private string FormatHardwarePresetTitle(PerformancePresetDescriptor preset)
    {
        string baseTitle = Loc.Get(preset.TitleLocKey);
        return preset.Id == _perfPresetSession.RecommendedPresetId
            ? $"{baseTitle} · {Loc.Get("settings.performance.preset.recommended")}"
            : baseTitle;
    }

    private PerfPresetRowHandle BuildUserBackupRowHandle(bool isActive) =>
        CreatePerformancePresetRowHandle(
            selectionId: VNotchNative.VN_PERF_SELECTION_BACKUP,
            isBackupRow: true,
            title: Loc.Get("settings.performance.preset.backup"),
            description: Loc.Get("settings.performance.preset.backup.desc", ResolveBackupTimeText()),
            isActive: isActive);

    private PerfPresetRowHandle BuildHardwarePresetRowHandle(PerformancePresetDescriptor preset, bool isActive) =>
        CreatePerformancePresetRowHandle(
            selectionId: preset.Id,
            isBackupRow: false,
            title: FormatHardwarePresetTitle(preset),
            description: Loc.Get(preset.DescriptionLocKey),
            isActive: isActive);

    private PerfPresetRowHandle CreatePerformancePresetRowHandle(
        int selectionId,
        bool isBackupRow,
        string title,
        string description,
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
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "PerfPresetStatusCol" });

        var accentScale = new ScaleTransform(1.0, isActive ? 1.0 : 0.35);
        var accentIndicator = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 2, 12, 2),
            Opacity = isActive ? 1.0 : 0.0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = accentScale,
        };
        accentIndicator.SetResourceReference(Border.BackgroundProperty, "AccentBrushStrong");
        Grid.SetColumn(accentIndicator, 0);
        rowGrid.Children.Add(accentIndicator);

        var titleBlock = new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SettingTitleText"),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var descriptionBlock = new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("SettingHintText"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        var copyStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        copyStack.Children.Add(titleBlock);
        copyStack.Children.Add(descriptionBlock);
        Grid.SetColumn(copyStack, 1);
        rowGrid.Children.Add(copyStack);

        var statusCell = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };

        var activeStatusTranslate = new TranslateTransform(isActive ? 0.0 : 8.0, 0.0);
        var activeStatusBlock = new TextBlock
        {
            Text = Loc.Get("settings.performance.preset.active"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = isActive ? 1.0 : 0.0,
            RenderTransform = activeStatusTranslate,
            IsHitTestVisible = false,
        };
        activeStatusBlock.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrushStrong");
        statusCell.Children.Add(activeStatusBlock);

        TextBlock? rollbackStatusBlock = null;
        TranslateTransform? rollbackStatusTranslate = null;
        if (isBackupRow)
        {
            rollbackStatusTranslate = new TranslateTransform(isActive ? -6.0 : 0.0, 0.0);
            rollbackStatusBlock = new TextBlock
            {
                Text = Loc.Get("settings.performance.preset.rollback"),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = isActive ? 0.0 : 1.0,
                RenderTransform = rollbackStatusTranslate,
                IsHitTestVisible = false,
            };
            rollbackStatusBlock.SetResourceReference(TextBlock.ForegroundProperty, "SoftText");
            statusCell.Children.Add(rollbackStatusBlock);
        }

        Grid.SetColumn(statusCell, 2);
        rowGrid.Children.Add(statusCell);

        button.Content = rowGrid;
        button.Click += PerformancePresetRow_Click;

        return new PerfPresetRowHandle
        {
            SelectionId = selectionId,
            IsBackupRow = isBackupRow,
            Button = button,
            AccentIndicator = accentIndicator,
            AccentScale = accentScale,
            TitleBlock = titleBlock,
            DescriptionBlock = descriptionBlock,
            ActiveStatusBlock = activeStatusBlock,
            ActiveStatusTranslate = activeStatusTranslate,
            RollbackStatusBlock = rollbackStatusBlock,
            RollbackStatusTranslate = rollbackStatusTranslate,
            IsActive = isActive,
        };
    }

    private static void ApplyRowContentAndSelection(
        PerfPresetRowHandle row,
        string title,
        string description,
        bool isActive,
        bool animate)
    {
        if (!string.Equals(row.TitleBlock.Text, title, StringComparison.Ordinal))
        {
            row.TitleBlock.Text = title;
            AutomationProperties.SetName(row.Button, title);
        }

        if (!string.Equals(row.DescriptionBlock.Text, description, StringComparison.Ordinal))
        {
            row.DescriptionBlock.Text = description;
        }

        if (row.IsActive == isActive)
            return;

        row.IsActive = isActive;
        row.Button.Uid = isActive ? "Active" : "Idle";

        if (!animate)
        {
            row.AccentIndicator.BeginAnimation(OpacityProperty, null);
            row.AccentScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            row.ActiveStatusBlock.BeginAnimation(OpacityProperty, null);
            row.ActiveStatusTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            row.AccentIndicator.Opacity = isActive ? 1.0 : 0.0;
            row.AccentScale.ScaleY = isActive ? 1.0 : 0.35;
            row.ActiveStatusBlock.Opacity = isActive ? 1.0 : 0.0;
            row.ActiveStatusTranslate.X = isActive ? 0.0 : 8.0;

            if (row.RollbackStatusBlock != null && row.RollbackStatusTranslate != null)
            {
                row.RollbackStatusBlock.BeginAnimation(OpacityProperty, null);
                row.RollbackStatusTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                row.RollbackStatusBlock.Opacity = isActive ? 0.0 : 1.0;
                row.RollbackStatusTranslate.X = isActive ? -6.0 : 0.0;
            }
            return;
        }

        int fadeDurationMs = isActive ? 220 : 150;
        int slideDurationMs = isActive ? 240 : 160;

        AnimateFluidProperty(row.AccentIndicator, OpacityProperty, isActive ? 1.0 : 0.0, fadeDurationMs);
        AnimateFluidProperty(row.AccentScale, ScaleTransform.ScaleYProperty, isActive ? 1.0 : 0.35, slideDurationMs);
        AnimateFluidProperty(row.ActiveStatusBlock, OpacityProperty, isActive ? 1.0 : 0.0, fadeDurationMs);
        AnimateFluidProperty(row.ActiveStatusTranslate, TranslateTransform.XProperty, isActive ? 0.0 : 8.0, slideDurationMs);

        if (row.RollbackStatusBlock != null && row.RollbackStatusTranslate != null)
        {
            AnimateFluidProperty(row.RollbackStatusBlock, OpacityProperty, isActive ? 0.0 : 1.0, isActive ? 150 : 220);
            AnimateFluidProperty(row.RollbackStatusTranslate, TranslateTransform.XProperty, isActive ? -6.0 : 0.0, isActive ? 160 : 240);
        }
    }

    private void SyncPerformancePresetSelectionIndicator(int activeSelection, bool animate)
    {
        if (PerformancePresetSelectionIndicator == null || PerformancePresetSelectionTranslate == null || PerformancePresetListPanel == null)
            return;

        PerfPresetRowHandle? activeRow = null;
        for (int i = 0; i < _perfPresetRows.Count; i++)
        {
            if (_perfPresetRows[i].SelectionId == activeSelection)
            {
                activeRow = _perfPresetRows[i];
                break;
            }
        }

        bool shouldAnimate = animate && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;

        if (activeRow == null || !PerformancePresetListPanel.IsVisible)
        {
            if (!shouldAnimate)
            {
                PerformancePresetSelectionIndicator.BeginAnimation(OpacityProperty, null);
                PerformancePresetSelectionIndicator.Opacity = 0.0;
            }
            else
            {
                AnimateFluidProperty(PerformancePresetSelectionIndicator, OpacityProperty, 0.0, 160);
            }
            return;
        }

        if (activeRow.Button.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                SyncPerformancePresetSelectionIndicator(activeSelection, animate: false);
            });
            return;
        }

        double targetY = activeRow.Button.TranslatePoint(new Point(0, 0), PerformancePresetListPanel).Y;
        double targetHeight = activeRow.Button.ActualHeight;
        bool wasHidden = PerformancePresetSelectionIndicator.Opacity < 0.05;

        if (!shouldAnimate)
        {
            PerformancePresetSelectionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            PerformancePresetSelectionIndicator.BeginAnimation(HeightProperty, null);
            PerformancePresetSelectionIndicator.BeginAnimation(OpacityProperty, null);
            PerformancePresetSelectionTranslate.Y = targetY;
            PerformancePresetSelectionIndicator.Height = targetHeight;
            PerformancePresetSelectionIndicator.Opacity = 1.0;
            return;
        }

        if (wasHidden)
        {
            PerformancePresetSelectionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            PerformancePresetSelectionIndicator.BeginAnimation(HeightProperty, null);
            PerformancePresetSelectionTranslate.Y = targetY;
            PerformancePresetSelectionIndicator.Height = targetHeight;
            AnimateFluidProperty(PerformancePresetSelectionIndicator, OpacityProperty, 1.0, 180);
            return;
        }

        AnimateFluidProperty(PerformancePresetSelectionTranslate, TranslateTransform.YProperty, targetY, 260);
        AnimateFluidProperty(PerformancePresetSelectionIndicator, HeightProperty, targetHeight, 260);
        AnimateFluidProperty(PerformancePresetSelectionIndicator, OpacityProperty, 1.0, 180);
    }

    private static void AnimateInsertedBackupRow(Button backupButton)
    {
        backupButton.Opacity = 0.0;
        var translate = new TranslateTransform(0.0, -8.0);
        backupButton.RenderTransform = translate;

        AnimateFluidProperty(backupButton, OpacityProperty, 1.0, 220);
        AnimateFluidProperty(translate, TranslateTransform.YProperty, 0.0, 240);
    }

    private static void AnimateFluidProperty(IAnimatable target, DependencyProperty property, double toValue, int durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = toValue,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = TrayMotion.EaseOut,
        };
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        target.BeginAnimation(property, anim);
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
        RebuildPerformancePresetRows(_settings, animateSelection: true);
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
