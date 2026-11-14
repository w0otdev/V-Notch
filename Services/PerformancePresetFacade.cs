using System;
using System.Collections.Generic;
using System.IO;
using VNotch.Models;

namespace VNotch.Services;

internal readonly record struct PerformancePresetDescriptor(
    int Id,
    string TitleLocKey,
    string DescriptionLocKey);

internal static class PerformancePresetCatalog
{
    public const string BackupFileName = "performance-user-backup.vns";
    private const string LiquidGlassStyle = "liquid-glass";
    private const string DefaultStyle = "default";

    // Single source of truth for UI preset entries; hardware values live in Native/Services/PerformancePresets.cpp.
    public static readonly IReadOnlyList<PerformancePresetDescriptor> All =
    [
        new(VNotchNative.VN_PERF_PRESET_ECO, "settings.performance.preset.eco", "settings.performance.preset.eco.desc"),
        new(VNotchNative.VN_PERF_PRESET_EVERYDAY, "settings.performance.preset.everyday", "settings.performance.preset.everyday.desc"),
        new(VNotchNative.VN_PERF_PRESET_BALANCED, "settings.performance.preset.balanced", "settings.performance.preset.balanced.desc"),
        new(VNotchNative.VN_PERF_PRESET_HIGH_END, "settings.performance.preset.high", "settings.performance.preset.high.desc"),
    ];

    public static bool IsValidPresetId(int presetId) =>
        presetId >= 0 && presetId < VNotchNative.VN_PERF_PRESET_COUNT;

    public static VnPerformancePresetState ToNativeState(NotchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        LiquidGlassConfig? glass = settings.LiquidGlass;
        bool isLiquidGlass = string.Equals(settings.NotchStyle, LiquidGlassStyle, StringComparison.OrdinalIgnoreCase);
        bool isHighPriority = string.Equals(
            ProcessPriorityPolicy.Normalize(settings.ProcessPriority),
            ProcessPriorityPolicy.High,
            StringComparison.OrdinalIgnoreCase);

        return new VnPerformancePresetState
        {
            AutoAnimationFps = ToByte(settings.AutoAnimationFps),
            AnimationFps = settings.AnimationFps,
            EnableBlurEffects = ToByte(settings.EnableBlurEffects),
            EnableSubjectBlur = ToByte(settings.EnableSubjectBlur),
            EnableSmartCrop = ToByte(settings.EnableSmartCrop),
            ShowMediaArtBackground = ToByte(settings.ShowMediaArtBackground),
            EnableShadow = ToByte(settings.EnableShadow),
            IsLiquidGlass = ToByte(isLiquidGlass),
            UseGpuRefraction = ToByte(glass is { UseGpuRefraction: true }),
            UseUltimateGpu = ToByte(glass is { UseUltimateGpu: true }),
            GlassTargetFps = glass?.TargetFps ?? 60,
            GpuPreference = Math.Clamp(settings.GpuPreference, 0, 2),
            ProcessPriority = isHighPriority ? 1 : 0,
        };
    }

    public static void ApplyNativeState(in VnPerformancePresetState nativeState, NotchSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.AutoAnimationFps = nativeState.AutoAnimationFps != 0;
        target.AnimationFps = nativeState.AnimationFps;
        target.EnableBlurEffects = nativeState.EnableBlurEffects != 0;
        target.EnableSubjectBlur = nativeState.EnableSubjectBlur != 0;
        target.EnableSmartCrop = nativeState.EnableSmartCrop != 0;
        target.ShowMediaArtBackground = nativeState.ShowMediaArtBackground != 0;
        target.EnableShadow = nativeState.EnableShadow != 0;

        if (nativeState.IsLiquidGlass == 0)
        {
            target.NotchStyle = DefaultStyle;
        }

        target.LiquidGlass ??= new LiquidGlassConfig();
        target.LiquidGlass.UseGpuRefraction = nativeState.UseGpuRefraction != 0;
        target.LiquidGlass.UseUltimateGpu = nativeState.UseUltimateGpu != 0;
        target.LiquidGlass.TargetFps = nativeState.GlassTargetFps;
        target.GpuPreference = nativeState.GpuPreference;
        target.ProcessPriority = nativeState.ProcessPriority == 1
            ? ProcessPriorityPolicy.High
            : ProcessPriorityPolicy.Normal;
    }

    public static int QueryRecommendedHardwareTier() =>
        VNotchNative.vn_perf_preset_recommend_hardware_tier(out int tier) == VNotchNative.VN_OK && IsValidPresetId(tier)
            ? tier
            : VNotchNative.VN_PERF_SELECTION_CUSTOM;

    private static byte ToByte(bool value) => value ? (byte)1 : (byte)0;
}

internal sealed class PerformancePresetSession
{
    private NotchSettings? _backupSnapshot;
    private NotchSettings? _lastAppliedPresetSnapshot;
    private bool _backupLoadedFromDiskEnvelope;
    private bool _diskBackupChecked;
    private int _preferredSelection = VNotchNative.VN_PERF_SELECTION_CUSTOM;

    public string? BackupFilePathOverride { get; set; }

    public bool HasUserBackup => _backupSnapshot != null;

    public DateTimeOffset BackupSavedAt { get; private set; }

    public int RecommendedPresetId { get; set; } = VNotchNative.VN_PERF_SELECTION_CUSTOM;

    public void EnsureDiskBackupLoaded(ISettingsApplicationService appService, NotchSettings currentSettings)
    {
        if (_diskBackupChecked || _backupSnapshot != null)
            return;

        _diskBackupChecked = true;
        string? backupPath = ResolveBackupFilePath(appService);
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            return;

        try
        {
            string rawJson = File.ReadAllText(backupPath);
            var (restored, _) = SettingsService.ImportSettingsFromString(rawJson, currentSettings);
            DateTime utcWrite = File.GetLastWriteTimeUtc(backupPath);

            _backupSnapshot = restored;
            BackupSavedAt = new DateTimeOffset(utcWrite, TimeSpan.Zero).ToLocalTime();
            _backupLoadedFromDiskEnvelope = true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("PERF-PRESET", $"Failed to load performance preset backup: {ex.Message}");
        }
    }

    public void NotifyExternalSettingsChanged(NotchSettings snapshot)
    {
        if (_lastAppliedPresetSnapshot != null && !snapshot.ValueEquals(_lastAppliedPresetSnapshot))
        {
            _preferredSelection = VNotchNative.VN_PERF_SELECTION_CUSTOM;
            return;
        }

        if (_preferredSelection == VNotchNative.VN_PERF_SELECTION_BACKUP &&
            (_backupSnapshot == null || !snapshot.ValueEquals(_backupSnapshot)))
        {
            _preferredSelection = VNotchNative.VN_PERF_SELECTION_CUSTOM;
        }
    }

    public int ResolveActiveSelection(NotchSettings snapshot)
    {
        DecideSelection(snapshot, out int activeSelection, out _);
        return activeSelection;
    }

    public bool TryPreparePresetSelection(
        int presetId,
        ISettingsApplicationService appService,
        NotchSettings currentSnapshot,
        out NotchSettings nextSnapshot,
        out bool capturedBackupNow)
    {
        nextSnapshot = currentSnapshot;
        capturedBackupNow = false;

        if (!PerformancePresetCatalog.IsValidPresetId(presetId))
            return false;

        EnsureDiskBackupLoaded(appService, currentSnapshot);
        DecideSelection(currentSnapshot, out _, out bool shouldBackup);

        VnPerformancePresetState currentNative = PerformancePresetCatalog.ToNativeState(currentSnapshot);
        if (VNotchNative.vn_perf_preset_apply(presetId, in currentNative, out VnPerformancePresetState nextNative) != VNotchNative.VN_OK)
            return false;

        if (shouldBackup)
        {
            CaptureUserBackup(appService, currentSnapshot);
            capturedBackupNow = true;
        }

        NotchSettings candidate = currentSnapshot.Clone();
        PerformancePresetCatalog.ApplyNativeState(in nextNative, candidate);

        _preferredSelection = presetId;
        _lastAppliedPresetSnapshot = null;
        nextSnapshot = candidate;
        return true;
    }

    public void CommitPresetApplied(NotchSettings appliedSettings)
    {
        _lastAppliedPresetSnapshot = appliedSettings.Clone();
    }

    public bool TryPrepareRollback(
        ISettingsApplicationService appService,
        NotchSettings currentSnapshot,
        out NotchSettings rollbackSnapshot)
    {
        rollbackSnapshot = currentSnapshot;
        EnsureDiskBackupLoaded(appService, currentSnapshot);

        if (_backupSnapshot == null)
            return false;

        rollbackSnapshot = MergeLiveSecretsIntoBackup(
            _backupSnapshot,
            currentSnapshot,
            _backupLoadedFromDiskEnvelope);

        _preferredSelection = VNotchNative.VN_PERF_SELECTION_BACKUP;
        _lastAppliedPresetSnapshot = null;
        return true;
    }

    public void CommitRollbackApplied(NotchSettings restoredSettings)
    {
        _backupSnapshot = restoredSettings.Clone();
        _backupLoadedFromDiskEnvelope = false;
    }

    private void DecideSelection(
        NotchSettings snapshot,
        out int activeSelection,
        out bool shouldBackupOnSelect)
    {
        VnPerformancePresetState perfState = PerformancePresetCatalog.ToNativeState(snapshot);
        byte hasBackup = _backupSnapshot != null ? (byte)1 : (byte)0;
        byte matchesBackup = (_backupSnapshot != null && snapshot.ValueEquals(_backupSnapshot)) ? (byte)1 : (byte)0;
        byte matchesLastPreset = (_lastAppliedPresetSnapshot != null && snapshot.ValueEquals(_lastAppliedPresetSnapshot)) ? (byte)1 : (byte)0;

        int status = VNotchNative.vn_perf_preset_decide_selection(
            in perfState,
            hasBackup,
            matchesBackup,
            matchesLastPreset,
            _preferredSelection,
            out activeSelection,
            out byte rawShouldBackup);

        if (status != VNotchNative.VN_OK)
        {
            activeSelection = VNotchNative.VN_PERF_SELECTION_CUSTOM;
            shouldBackupOnSelect = hasBackup == 0;
            return;
        }

        shouldBackupOnSelect = rawShouldBackup != 0;
    }

    private void CaptureUserBackup(ISettingsApplicationService appService, NotchSettings snapshot)
    {
        _backupSnapshot = snapshot.Clone();
        BackupSavedAt = DateTimeOffset.Now;
        _backupLoadedFromDiskEnvelope = false;

        string? backupPath = ResolveBackupFilePath(appService);
        if (string.IsNullOrWhiteSpace(backupPath))
            return;

        try
        {
            appService.Export(backupPath, snapshot);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("PERF-PRESET", $"Could not persist .vns backup to disk: {ex.Message}");
        }
    }

    private string? ResolveBackupFilePath(ISettingsApplicationService appService)
    {
        if (!string.IsNullOrWhiteSpace(BackupFilePathOverride))
            return BackupFilePathOverride;

        if (appService is SettingsApplicationService { PresetBackupDirectory: { Length: > 0 } dir })
            return Path.Combine(dir, PerformancePresetCatalog.BackupFileName);

        return null;
    }

    private static NotchSettings MergeLiveSecretsIntoBackup(
        NotchSettings backup,
        NotchSettings current,
        bool loadedFromDiskEnvelope)
    {
        NotchSettings target = backup.Clone();
        target.SpotlightOpenAIApiKey = current.SpotlightOpenAIApiKey;
        target.SpotlightGeminiApiKey = current.SpotlightGeminiApiKey;
        target.SpotlightClaudeApiKey = current.SpotlightClaudeApiKey;
        target.SpotlightDeepSeekApiKey = current.SpotlightDeepSeekApiKey;
        target.YouTubeApiKey = current.YouTubeApiKey;
        target.SpotifySpDc = current.SpotifySpDc;
        target.CodexUsageExecutable = current.CodexUsageExecutable;

        if (!loadedFromDiskEnvelope)
            return target;

        target.EnableAiUsageMonitoring = current.EnableAiUsageMonitoring;
        if (SpotifyCanvasConsent.HasAccepted(current))
        {
            target.SpotifyCanvasConsentVersion = current.SpotifyCanvasConsentVersion;
            target.EnableSpotifyCanvas = current.EnableSpotifyCanvas;
        }

        return target;
    }
}
