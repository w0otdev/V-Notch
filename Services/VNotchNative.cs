using System;
using System.Runtime.InteropServices;

namespace VNotch.Services;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VnMarkdownSlice
{
    public int Start;
    public int Len;
}

public static partial class VNotchNative
{
    private const string DllName = "VNotch.Native.dll";

    public const int VN_OK = 0;
    public const int VN_ERR_INVALID_ARG = -1;
    public const int VN_ERR_BUFFER_TOO_SMALL = -2;
    public const int VN_ERR_FAIL = -3;

    internal const int VN_PERF_SELECTION_BACKUP = -2;
    internal const int VN_PERF_SELECTION_CUSTOM = -1;
    internal const int VN_PERF_PRESET_ECO = 0;
    internal const int VN_PERF_PRESET_EVERYDAY = 1;
    internal const int VN_PERF_PRESET_BALANCED = 2;
    internal const int VN_PERF_PRESET_HIGH_END = 3;
    internal const int VN_PERF_PRESET_COUNT = 4;

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_settings_search_normalize(
        ReadOnlySpan<char> inputPtr,
        int inputLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_settings_search_score_row(
        ReadOnlySpan<char> normTitlePtr,
        int normTitleLen,
        ReadOnlySpan<char> normContentPtr,
        int normContentLen,
        ReadOnlySpan<char> normCombinedPtr,
        int normCombinedLen,
        ReadOnlySpan<char> normQueryPtr,
        int normQueryLen,
        byte allowFuzzy,
        out int outScore);

    [LibraryImport(DllName)]
    internal static partial int vn_nav_tabs_calculate_target_slot_hysteresis(
        int current_target,
        double visual_pos,
        double pitch,
        int total_slots,
        out int out_slot);

    [LibraryImport(DllName)]
    internal static partial int vn_nav_tabs_calculate_neighbor_offset(
        int index,
        int initial_slot,
        int target_slot,
        double pitch,
        out double out_offset);

    [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int vn_media_detection_build_session_instance_key(
        nint unkPtr,
        ReadOnlySpan<char> sourceApp,
        int sourceAppLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(DllName)]
    internal static partial int vn_ai_usage_compute_window_visual(
        double usedPercent,
        int durationMinutes,
        byte hasResetsAt,
        long resetsAtUtcTicks,
        byte hasUpdatedAt,
        long updatedAtUtcTicks,
        long nowUtcTicks,
        out VnAiQuotaWindowVisualOut outVisual);

    [LibraryImport(DllName)]
    internal static partial int vn_ai_usage_compute_card_summary(
        int state,
        int windowCount,
        byte hasUpdatedAt,
        long updatedAtUtcTicks,
        long nowUtcTicks,
        out VnAiProviderCardSummaryOut outSummary);

    [LibraryImport(DllName)]
    internal static partial int vn_ai_usage_resolve_claude_setup_state(
        int usageState,
        byte hasSettingsFile,
        byte hasStatusLine,
        byte matchesBridgeCommand,
        byte hasCustomApiEndpoint,
        out byte outActionKind,
        out int outPromptMode);

    [LibraryImport(DllName)]
    internal static partial int vn_perf_preset_apply(
        int presetId,
        in VnPerformancePresetState currentState,
        out VnPerformancePresetState outState);

    [LibraryImport(DllName)]
    internal static partial int vn_perf_preset_match(
        in VnPerformancePresetState state,
        out int outPresetId);

    [LibraryImport(DllName)]
    internal static partial int vn_perf_preset_decide_selection(
        in VnPerformancePresetState currentState,
        byte hasBackup,
        byte matchesBackupFull,
        byte matchesLastPresetFull,
        int preferredSelection,
        out int outActiveItem,
        out byte outShouldBackupOnPresetSelect);

    [LibraryImport(DllName)]
    internal static partial int vn_perf_preset_recommend_hardware_tier(
        out int outPresetId);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct VnPerformancePresetState
{
    public byte AutoAnimationFps;
    public int AnimationFps;
    public byte EnableBlurEffects;
    public byte EnableSubjectBlur;
    public byte EnableSmartCrop;
    public byte ShowMediaArtBackground;
    public byte EnableShadow;
    public byte IsLiquidGlass;
    public byte UseGpuRefraction;
    public byte UseUltimateGpu;
    public int GlassTargetFps;
    public int GpuPreference;
    public int ProcessPriority;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct VnAiQuotaWindowVisualOut
{
    public double ClampedUsedPercent;
    public double ClampedRemainingPercent;
    public double RowOpacity;
    public byte IsExpired;
    public byte IsStale;
    public byte SeverityTier;
    public byte ResetMode;
    public int ResetHours;
    public int ResetMinutes;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct VnAiProviderCardSummaryOut
{
    public byte ShowQuotaRows;
    public int VisibleWindowCount;
    public byte IsStale;
    public byte ActionKind;
}
