#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>

namespace vnotch::services {

class PerformanceDiagnosticService final {
public:
    PerformanceDiagnosticService() = delete;

    [[nodiscard]] static constexpr uint64_t SafeDelta(uint64_t current, uint64_t previous) noexcept {
        return current > previous ? current - previous : 0ULL;
    }

    [[nodiscard]] static constexpr double ComputeProcessCpu(
        uint64_t proc_total_time,
        uint64_t last_proc_time,
        int64_t now_ticks,
        int64_t last_proc_ticks,
        int64_t stopwatch_freq,
        int32_t processor_count,
        double smoothed_proc_cpu) noexcept {
        if (last_proc_ticks <= 0 || stopwatch_freq <= 0 || processor_count <= 0) {
            return smoothed_proc_cpu;
        }
        const uint64_t delta_proc = SafeDelta(proc_total_time, last_proc_time);
        const double delta_wall_sec = static_cast<double>(now_ticks - last_proc_ticks) / static_cast<double>(stopwatch_freq);
        if (delta_wall_sec <= 0.005) {
            return smoothed_proc_cpu;
        }
        const double proc_sec = static_cast<double>(delta_proc) / 10000000.0;
        const double raw_proc_cpu = std::clamp((proc_sec / (delta_wall_sec * static_cast<double>(processor_count))) * 100.0, 0.0, 100.0);
        return last_proc_time == 0ULL ? raw_proc_cpu : (smoothed_proc_cpu * 0.88 + raw_proc_cpu * 0.12);
    }

    [[nodiscard]] static constexpr bool ComputeGlobalCpu(
        uint64_t idle,
        uint64_t last_sys_idle,
        uint64_t kernel,
        uint64_t last_sys_kernel,
        uint64_t user,
        uint64_t last_sys_user,
        bool has_global_cpu_measurement,
        double smoothed_global_cpu,
        double& out_smoothed_global_cpu) noexcept {
        out_smoothed_global_cpu = smoothed_global_cpu;
        if (last_sys_idle == 0ULL) {
            return has_global_cpu_measurement;
        }
        const uint64_t delta_idle = SafeDelta(idle, last_sys_idle);
        const uint64_t delta_total = SafeDelta(kernel, last_sys_kernel) + SafeDelta(user, last_sys_user);
        if (delta_total == 0ULL) {
            return has_global_cpu_measurement;
        }
        const double busy = delta_total > delta_idle ? static_cast<double>(delta_total - delta_idle) : 0.0;
        const double raw_global_cpu = std::clamp((busy / static_cast<double>(delta_total)) * 100.0, 0.0, 100.0);
        out_smoothed_global_cpu = has_global_cpu_measurement
            ? (smoothed_global_cpu * 0.88 + raw_global_cpu * 0.12)
            : raw_global_cpu;
        return true;
    }

    [[nodiscard]] static constexpr bool ComputeSmoothedAllocRate(
        int64_t current_total_alloc,
        int64_t last_allocated_bytes,
        int64_t now_ticks,
        int64_t last_alloc_sample_ticks,
        int64_t stopwatch_freq,
        double smoothed_alloc_bytes_per_sec,
        double& out_smoothed_alloc_bytes_per_sec) noexcept {
        out_smoothed_alloc_bytes_per_sec = smoothed_alloc_bytes_per_sec;
        if (stopwatch_freq <= 0) {
            return false;
        }
        const double alloc_delta_sec = static_cast<double>(now_ticks - last_alloc_sample_ticks) / static_cast<double>(stopwatch_freq);
        if (alloc_delta_sec < 0.2) {
            return false;
        }
        const int64_t alloc_delta_bytes = current_total_alloc - last_allocated_bytes;
        const double rate = alloc_delta_bytes > 0 ? static_cast<double>(alloc_delta_bytes) / alloc_delta_sec : 0.0;
        out_smoothed_alloc_bytes_per_sec = smoothed_alloc_bytes_per_sec * 0.7 + rate * 0.3;
        return true;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_perf_diag_compute_process_cpu(
    uint64_t proc_total_time,
    uint64_t last_proc_time,
    int64_t now_ticks,
    int64_t last_proc_ticks,
    int64_t stopwatch_freq,
    int32_t processor_count,
    double smoothed_proc_cpu,
    double* out_smoothed_proc_cpu) noexcept {
    if (!out_smoothed_proc_cpu) return VN_E_INVALID_ARG;
    *out_smoothed_proc_cpu = vnotch::services::PerformanceDiagnosticService::ComputeProcessCpu(
        proc_total_time, last_proc_time, now_ticks, last_proc_ticks, stopwatch_freq, processor_count, smoothed_proc_cpu);
    return VN_OK;
}

VN_EXPORT int32_t vn_perf_diag_compute_global_cpu(
    uint64_t idle,
    uint64_t last_sys_idle,
    uint64_t kernel,
    uint64_t last_sys_kernel,
    uint64_t user,
    uint64_t last_sys_user,
    uint8_t has_global_cpu_measurement,
    double smoothed_global_cpu,
    double* out_smoothed_global_cpu,
    uint8_t* out_has_global_cpu_measurement) noexcept {
    if (!out_smoothed_global_cpu || !out_has_global_cpu_measurement) return VN_E_INVALID_ARG;
    double res = 0.0;
    const bool has_meas = vnotch::services::PerformanceDiagnosticService::ComputeGlobalCpu(
        idle, last_sys_idle, kernel, last_sys_kernel, user, last_sys_user, has_global_cpu_measurement != 0, smoothed_global_cpu, res);
    *out_smoothed_global_cpu = res;
    *out_has_global_cpu_measurement = has_meas ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_perf_diag_compute_smoothed_alloc_rate(
    int64_t current_total_alloc,
    int64_t last_allocated_bytes,
    int64_t now_ticks,
    int64_t last_alloc_sample_ticks,
    int64_t stopwatch_freq,
    double smoothed_alloc_bytes_per_sec,
    double* out_smoothed_alloc_bytes_per_sec,
    uint8_t* out_updated) noexcept {
    if (!out_smoothed_alloc_bytes_per_sec || !out_updated) return VN_E_INVALID_ARG;
    double res = 0.0;
    const bool updated = vnotch::services::PerformanceDiagnosticService::ComputeSmoothedAllocRate(
        current_total_alloc, last_allocated_bytes, now_ticks, last_alloc_sample_ticks, stopwatch_freq, smoothed_alloc_bytes_per_sec, res);
    *out_smoothed_alloc_bytes_per_sec = res;
    *out_updated = updated ? 1 : 0;
    return VN_OK;
}

} // extern "C"
