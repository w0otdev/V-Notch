#include "../vnotch_native.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <array>
#include <atomic>

namespace
{
    enum class GpuPref : int32_t
    {
        Auto = 0,
        PowerSaving = 1,
        HighPerformance = 2,
    };

    enum class ProcPriority : int32_t
    {
        Normal = 0,
        High = 1,
    };

    struct PresetSpec
    {
        VnPerformancePresetId id;
        bool auto_animation_fps;
        int32_t animation_fps;
        bool enable_blur_effects;
        bool enable_subject_blur;
        bool enable_smart_crop;
        bool show_media_art_background;
        bool enable_shadow;
        bool keep_liquid_glass;
        bool use_gpu_refraction;
        bool use_ultimate_gpu;
        int32_t glass_target_fps;
        GpuPref gpu_preference;
        ProcPriority process_priority;
    };

    struct HardwareProfile
    {
        uint64_t ram_gib;
        uint32_t logical_cores;
        uint64_t dedicated_vram_mib;
        bool has_battery;
    };

    // Add or tune hardware-tier presets in one declarative table.
    constexpr std::array<PresetSpec, VN_PERF_PRESET_COUNT> kPresets = {{
        PresetSpec{
            .id = VN_PERF_PRESET_ECO,
            .auto_animation_fps = false,
            .animation_fps = 60,
            .enable_blur_effects = false,
            .enable_subject_blur = false,
            .enable_smart_crop = false,
            .show_media_art_background = false,
            .enable_shadow = false,
            .keep_liquid_glass = false,
            .use_gpu_refraction = false,
            .use_ultimate_gpu = false,
            .glass_target_fps = 30,
            .gpu_preference = GpuPref::PowerSaving,
            .process_priority = ProcPriority::Normal,
        },
        PresetSpec{
            .id = VN_PERF_PRESET_EVERYDAY,
            .auto_animation_fps = false,
            .animation_fps = 60,
            .enable_blur_effects = true,
            .enable_subject_blur = false,
            .enable_smart_crop = false,
            .show_media_art_background = true,
            .enable_shadow = false,
            .keep_liquid_glass = false,
            .use_gpu_refraction = false,
            .use_ultimate_gpu = false,
            .glass_target_fps = 60,
            .gpu_preference = GpuPref::PowerSaving,
            .process_priority = ProcPriority::Normal,
        },
        PresetSpec{
            .id = VN_PERF_PRESET_BALANCED,
            .auto_animation_fps = true,
            .animation_fps = 120,
            .enable_blur_effects = true,
            .enable_subject_blur = true,
            .enable_smart_crop = true,
            .show_media_art_background = true,
            .enable_shadow = true,
            .keep_liquid_glass = true,
            .use_gpu_refraction = true,
            .use_ultimate_gpu = false,
            .glass_target_fps = 60,
            .gpu_preference = GpuPref::Auto,
            .process_priority = ProcPriority::Normal,
        },
        PresetSpec{
            .id = VN_PERF_PRESET_HIGH_END,
            .auto_animation_fps = true,
            .animation_fps = 240,
            .enable_blur_effects = true,
            .enable_subject_blur = true,
            .enable_smart_crop = true,
            .show_media_art_background = true,
            .enable_shadow = true,
            .keep_liquid_glass = true,
            .use_gpu_refraction = true,
            .use_ultimate_gpu = true,
            .glass_target_fps = 120,
            .gpu_preference = GpuPref::HighPerformance,
            .process_priority = ProcPriority::High,
        },
    }};

    [[nodiscard]] constexpr uint8_t ToFlag(bool value) noexcept
    {
        return value ? 1u : 0u;
    }

    [[nodiscard]] constexpr bool IsValidPresetId(int32_t preset_id) noexcept
    {
        return preset_id >= 0 && preset_id < VN_PERF_PRESET_COUNT;
    }

    [[nodiscard]] constexpr VnPerformancePresetState NormalizeState(const VnPerformancePresetState& raw) noexcept
    {
        VnPerformancePresetState out{};
        out.auto_animation_fps = ToFlag(raw.auto_animation_fps != 0);
        out.animation_fps = raw.animation_fps;
        out.enable_blur_effects = ToFlag(raw.enable_blur_effects != 0);
        out.enable_subject_blur = ToFlag(raw.enable_subject_blur != 0);
        out.enable_smart_crop = ToFlag(raw.enable_smart_crop != 0);
        out.show_media_art_background = ToFlag(raw.show_media_art_background != 0);
        out.enable_shadow = ToFlag(raw.enable_shadow != 0);
        out.is_liquid_glass = ToFlag(raw.is_liquid_glass != 0);
        out.use_gpu_refraction = ToFlag(raw.use_gpu_refraction != 0);
        out.use_ultimate_gpu = ToFlag(raw.use_ultimate_gpu != 0);
        out.glass_target_fps = raw.glass_target_fps;
        out.gpu_preference = (raw.gpu_preference >= 0 && raw.gpu_preference <= 2) ? raw.gpu_preference : 0;
        out.process_priority = raw.process_priority == 1 ? 1 : 0;
        return out;
    }

    constexpr void ApplyLiquidGlassPolicy(
        const PresetSpec& spec,
        const VnPerformancePresetState& base,
        VnPerformancePresetState& target) noexcept
    {
        target.is_liquid_glass = spec.keep_liquid_glass ? base.is_liquid_glass : 0;

        if (target.is_liquid_glass != 0)
        {
            target.use_gpu_refraction = ToFlag(spec.use_gpu_refraction);
            target.use_ultimate_gpu = ToFlag(spec.use_ultimate_gpu);
            target.glass_target_fps = spec.glass_target_fps;
            return;
        }

        if (!spec.keep_liquid_glass)
        {
            target.use_gpu_refraction = 0;
            target.use_ultimate_gpu = 0;
            target.glass_target_fps = spec.glass_target_fps;
            return;
        }

        target.use_gpu_refraction = base.use_gpu_refraction;
        target.use_ultimate_gpu = spec.use_ultimate_gpu ? base.use_ultimate_gpu : 0;
        target.glass_target_fps = base.glass_target_fps;
    }

    [[nodiscard]] constexpr VnPerformancePresetState ApplySpec(
        const PresetSpec& spec,
        const VnPerformancePresetState& current) noexcept
    {
        const VnPerformancePresetState base = NormalizeState(current);

        VnPerformancePresetState next{};
        next.auto_animation_fps = ToFlag(spec.auto_animation_fps);
        next.animation_fps = spec.animation_fps;
        next.enable_blur_effects = ToFlag(spec.enable_blur_effects);
        next.enable_subject_blur = ToFlag(spec.enable_subject_blur);
        next.enable_smart_crop = ToFlag(spec.enable_smart_crop);
        next.show_media_art_background = ToFlag(spec.show_media_art_background);
        next.enable_shadow = ToFlag(spec.enable_shadow);
        ApplyLiquidGlassPolicy(spec, base, next);
        next.gpu_preference = static_cast<int32_t>(spec.gpu_preference);
        next.process_priority = static_cast<int32_t>(spec.process_priority);
        return next;
    }

    [[nodiscard]] constexpr bool StateEquals(
        const VnPerformancePresetState& lhs_raw,
        const VnPerformancePresetState& rhs_raw) noexcept
    {
        const VnPerformancePresetState lhs = NormalizeState(lhs_raw);
        const VnPerformancePresetState rhs = NormalizeState(rhs_raw);

        const bool core_matches =
            lhs.auto_animation_fps == rhs.auto_animation_fps &&
            lhs.animation_fps == rhs.animation_fps &&
            lhs.enable_blur_effects == rhs.enable_blur_effects &&
            lhs.enable_subject_blur == rhs.enable_subject_blur &&
            lhs.enable_smart_crop == rhs.enable_smart_crop &&
            lhs.show_media_art_background == rhs.show_media_art_background &&
            lhs.enable_shadow == rhs.enable_shadow &&
            lhs.is_liquid_glass == rhs.is_liquid_glass &&
            lhs.gpu_preference == rhs.gpu_preference &&
            lhs.process_priority == rhs.process_priority;

        if (!core_matches)
        {
            return false;
        }

        if (lhs.is_liquid_glass == 0)
        {
            return true;
        }

        return lhs.use_gpu_refraction == rhs.use_gpu_refraction &&
               lhs.use_ultimate_gpu == rhs.use_ultimate_gpu &&
               lhs.glass_target_fps == rhs.glass_target_fps;
    }

    [[nodiscard]] constexpr int32_t MatchPreset(const VnPerformancePresetState& state) noexcept
    {
        for (size_t i = 0; i < kPresets.size(); ++i)
        {
            if (StateEquals(state, ApplySpec(kPresets[i], state)))
            {
                return static_cast<int32_t>(kPresets[i].id);
            }
        }
        return VN_PERF_SELECTION_CUSTOM;
    }

    [[nodiscard]] constexpr int32_t ResolveActiveSelection(
        int32_t matched_preset,
        bool backup_exact,
        bool preset_exact,
        int32_t preferred_selection) noexcept
    {
        if (preferred_selection == VN_PERF_SELECTION_BACKUP && backup_exact)
        {
            return VN_PERF_SELECTION_BACKUP;
        }

        if (IsValidPresetId(preferred_selection) &&
            matched_preset == preferred_selection &&
            (preset_exact || !backup_exact))
        {
            return matched_preset;
        }

        if (backup_exact)
        {
            return VN_PERF_SELECTION_BACKUP;
        }

        return matched_preset;
    }

    [[nodiscard]] constexpr bool ShouldCaptureUserBackup(
        int32_t matched_preset,
        bool backup_present,
        bool backup_exact,
        bool preset_exact,
        int32_t preferred_selection) noexcept
    {
        if (!backup_present)
        {
            return true;
        }

        if (backup_exact || preset_exact)
        {
            return false;
        }

        // Reopened settings on an unmodified built-in preset while a user backup already exists.
        if (IsValidPresetId(matched_preset) && preferred_selection == VN_PERF_SELECTION_CUSTOM)
        {
            return false;
        }

        return true;
    }

    [[nodiscard]] constexpr int32_t ClassifyHardwareTier(const HardwareProfile& hw) noexcept
    {
        if (hw.ram_gib <= 8 || (hw.logical_cores > 0u && hw.logical_cores <= 4u))
        {
            return VN_PERF_PRESET_ECO;
        }

        if (hw.dedicated_vram_mib >= 4096 && hw.ram_gib >= 16 && hw.logical_cores >= 8u)
        {
            return VN_PERF_PRESET_HIGH_END;
        }

        if (hw.has_battery && hw.dedicated_vram_mib < 2048)
        {
            return VN_PERF_PRESET_EVERYDAY;
        }

        return VN_PERF_PRESET_BALANCED;
    }

    static_assert(ClassifyHardwareTier({ 8, 8, 4096, false }) == VN_PERF_PRESET_ECO);
    static_assert(ClassifyHardwareTier({ 16, 8, 512, true }) == VN_PERF_PRESET_EVERYDAY);
    static_assert(ClassifyHardwareTier({ 16, 12, 0, false }) == VN_PERF_PRESET_BALANCED);
    static_assert(ClassifyHardwareTier({ 32, 16, 8192, true }) == VN_PERF_PRESET_HIGH_END);

    [[nodiscard]] HardwareProfile ProbeHardwareProfile() noexcept
    {
        HardwareProfile profile{
            .ram_gib = 16,
            .logical_cores = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS),
            .dedicated_vram_mib = 0,
            .has_battery = false,
        };

        MEMORYSTATUSEX mem{};
        mem.dwLength = sizeof(mem);
        if (GlobalMemoryStatusEx(&mem) != 0)
        {
            profile.ram_gib = (mem.ullTotalPhys + (512ULL << 20)) >> 30;
        }

        SYSTEM_POWER_STATUS power{};
        if (GetSystemPowerStatus(&power) != 0)
        {
            profile.has_battery = (power.BatteryFlag != 128 && power.BatteryFlag != 255);
        }

        wchar_t adapter_name[128]{};
        int32_t adapter_len = 0;
        uint64_t dedicated_vram_bytes = 0;
        (void)vn_gpu_monitor_find_best_adapter(adapter_name, 128, &adapter_len, &dedicated_vram_bytes);
        profile.dedicated_vram_mib = dedicated_vram_bytes >> 20;

        return profile;
    }

    std::atomic<int32_t> g_cached_recommended_tier{ VN_PERF_SELECTION_CUSTOM };
}

VN_EXPORT int32_t __stdcall vn_perf_preset_apply(
    int32_t preset_id,
    const VnPerformancePresetState* current_state,
    VnPerformancePresetState* out_state) noexcept
{
    if (current_state == nullptr || out_state == nullptr || !IsValidPresetId(preset_id))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_state = ApplySpec(kPresets[static_cast<size_t>(preset_id)], *current_state);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_perf_preset_match(
    const VnPerformancePresetState* state,
    int32_t* out_preset_id) noexcept
{
    if (state == nullptr || out_preset_id == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_preset_id = MatchPreset(*state);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_perf_preset_decide_selection(
    const VnPerformancePresetState* current_state,
    uint8_t has_backup,
    uint8_t matches_backup_full,
    uint8_t matches_last_preset_full,
    int32_t preferred_selection,
    int32_t* out_active_item,
    uint8_t* out_should_backup_on_preset_select) noexcept
{
    if (current_state == nullptr || out_active_item == nullptr || out_should_backup_on_preset_select == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    const int32_t matched_preset = MatchPreset(*current_state);
    const bool backup_present = has_backup != 0;
    const bool backup_exact = backup_present && (matches_backup_full != 0);
    const bool preset_exact = matches_last_preset_full != 0;

    *out_active_item = ResolveActiveSelection(matched_preset, backup_exact, preset_exact, preferred_selection);
    *out_should_backup_on_preset_select = ToFlag(ShouldCaptureUserBackup(
        matched_preset,
        backup_present,
        backup_exact,
        preset_exact,
        preferred_selection));
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_perf_preset_recommend_hardware_tier(
    int32_t* out_preset_id) noexcept
{
    if (out_preset_id == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    const int32_t cached = g_cached_recommended_tier.load(std::memory_order_acquire);
    if (IsValidPresetId(cached))
    {
        *out_preset_id = cached;
        return VN_OK;
    }

    const int32_t tier = ClassifyHardwareTier(ProbeHardwareProfile());
    g_cached_recommended_tier.store(tier, std::memory_order_release);
    *out_preset_id = tier;
    return VN_OK;
}
