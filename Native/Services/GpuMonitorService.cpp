#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class GpuMonitorService final {
public:
    GpuMonitorService() = delete;

    [[nodiscard]] static constexpr int64_t ScoreAdapter(std::u16string_view name, uint64_t vram_bytes) noexcept {
        int64_t score = static_cast<int64_t>(vram_bytes / (1024ULL * 1024ULL));
        if (vn_contains_icase_ascii(name, u"NVIDIA") ||
            vn_contains_icase_ascii(name, u"GeForce") ||
            vn_contains_icase_ascii(name, u"RTX") ||
            vn_contains_icase_ascii(name, u"GTX") ||
            vn_contains_icase_ascii(name, u"Radeon RX") ||
            vn_contains_icase_ascii(name, u"Arc")) {
            score += 100000;
        }
        return score;
    }

    [[nodiscard]] static constexpr double ComputeProcessCpuDelta(
        uint64_t proc_total_time,
        uint64_t last_proc_time,
        int64_t now_ticks,
        int64_t last_proc_ticks,
        int64_t stopwatch_freq,
        int32_t processor_count,
        double smoothed_proc_cpu) noexcept {
        if (last_proc_ticks == 0 || stopwatch_freq <= 0 || processor_count <= 0) {
            return 0.0;
        }

        const uint64_t delta_proc = proc_total_time > last_proc_time ? proc_total_time - last_proc_time : 0ULL;
        const double delta_wall_sec = static_cast<double>(now_ticks - last_proc_ticks) / static_cast<double>(stopwatch_freq);
        if (delta_wall_sec <= 0.005) {
            return smoothed_proc_cpu;
        }

        const double proc_sec = static_cast<double>(delta_proc) / 10000000.0;
        const double raw_proc_cpu = std::clamp((proc_sec / (delta_wall_sec * static_cast<double>(processor_count))) * 100.0, 0.0, 100.0);
        return last_proc_time == 0ULL ? raw_proc_cpu : (smoothed_proc_cpu * 0.88 + raw_proc_cpu * 0.12);
    }

    [[nodiscard]] static constexpr double ComputeGlobalCpuDelta(
        uint64_t idle,
        uint64_t last_sys_idle,
        uint64_t kernel,
        uint64_t last_sys_kernel,
        uint64_t user,
        uint64_t last_sys_user,
        double smoothed_global_cpu) noexcept {
        if (last_sys_idle == 0ULL) {
            return 0.0;
        }

        const uint64_t delta_idle = idle > last_sys_idle ? idle - last_sys_idle : 0ULL;
        const uint64_t delta_kernel = kernel > last_sys_kernel ? kernel - last_sys_kernel : 0ULL;
        const uint64_t delta_user = user > last_sys_user ? user - last_sys_user : 0ULL;
        const uint64_t delta_total = delta_kernel + delta_user;

        if (delta_total == 0ULL) {
            return smoothed_global_cpu;
        }

        const double busy = delta_total > delta_idle ? static_cast<double>(delta_total - delta_idle) : 0.0;
        const double raw_global_cpu = std::clamp((busy / static_cast<double>(delta_total)) * 100.0, 0.0, 100.0);
        return smoothed_global_cpu * 0.88 + raw_global_cpu * 0.12;
    }

    [[nodiscard]] static constexpr bool IsTargetGpuEngineInstance(std::u16string_view inst) noexcept {
        return vn_contains_icase_ascii(inst, u"engtype_3D") ||
               vn_contains_icase_ascii(inst, u"engtype_Compute") ||
               vn_contains_icase_ascii(inst, u"engtype_VR");
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_gpu_monitor_score_adapter(
    const char16_t* name,
    int32_t name_len,
    uint64_t vram_bytes,
    int64_t* out_score) noexcept {
    if (!out_score || (!name && name_len > 0) || name_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(name ? name : u"", static_cast<size_t>(name_len));
    *out_score = vnotch::services::GpuMonitorService::ScoreAdapter(sv, vram_bytes);
    return VN_OK;
}

VN_EXPORT int32_t vn_gpu_monitor_compute_process_cpu_delta(
    uint64_t proc_total_time,
    uint64_t last_proc_time,
    int64_t now_ticks,
    int64_t last_proc_ticks,
    int64_t stopwatch_freq,
    int32_t processor_count,
    double smoothed_proc_cpu,
    double* out_cpu) noexcept {
    if (!out_cpu) return VN_E_INVALID_ARG;
    *out_cpu = vnotch::services::GpuMonitorService::ComputeProcessCpuDelta(
        proc_total_time, last_proc_time, now_ticks, last_proc_ticks, stopwatch_freq, processor_count, smoothed_proc_cpu);
    return VN_OK;
}

VN_EXPORT int32_t vn_gpu_monitor_compute_global_cpu_delta(
    uint64_t idle,
    uint64_t last_sys_idle,
    uint64_t kernel,
    uint64_t last_sys_kernel,
    uint64_t user,
    uint64_t last_sys_user,
    double smoothed_global_cpu,
    double* out_cpu) noexcept {
    if (!out_cpu) return VN_E_INVALID_ARG;
    *out_cpu = vnotch::services::GpuMonitorService::ComputeGlobalCpuDelta(
        idle, last_sys_idle, kernel, last_sys_kernel, user, last_sys_user, smoothed_global_cpu);
    return VN_OK;
}

VN_EXPORT int32_t vn_gpu_monitor_is_target_engine_instance(
    const char16_t* inst,
    int32_t inst_len,
    uint8_t* out_is_target) noexcept {
    if (!out_is_target || (!inst && inst_len > 0) || inst_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(inst ? inst : u"", static_cast<size_t>(inst_len));
    *out_is_target = vnotch::services::GpuMonitorService::IsTargetGpuEngineInstance(sv) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
