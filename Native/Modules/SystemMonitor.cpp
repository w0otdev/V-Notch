#include "../vnotch_common.h"
#include <winsock2.h>
#include <ws2ipdef.h>
#include <iphlpapi.h>
#include <algorithm>
#include <cstdint>

#pragma comment(lib, "iphlpapi.lib")

// System monitor sampling (CPU, RAM, network throughput). The C# module only schedules
// ticks and forwards the numbers; all counters and smoothing state live here.

namespace vnotch::modules {

#pragma pack(push, 1)
struct VnSystemMonitorState {
    uint64_t last_idle;
    uint64_t last_kernel;
    uint64_t last_user;
    int64_t last_cpu_qpc;
    double smoothed_cpu;
    uint8_t has_cpu_measurement;
    uint64_t last_recv_bytes;
    uint64_t last_sent_bytes;
    int64_t last_net_qpc;
    double down_bytes_per_sec;
    double up_bytes_per_sec;
};

struct VnSystemMemory {
    uint64_t total_physical;
    uint64_t available_physical;
    uint32_t memory_load_percent;
    uint64_t installed_physical;
};
#pragma pack(pop)

namespace {

constexpr double kMinCpuSampleMs = 50.0;
constexpr double kMinNetSampleSeconds = 0.1;
constexpr double kCpuSmoothing = 0.85;
constexpr ULONG kIfTypeSoftwareLoopback = 24;
constexpr ULONG kIfTypeTunnel = 131;

int64_t QueryCounter() noexcept {
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}

int64_t QueryFrequency() noexcept {
    LARGE_INTEGER value{};
    QueryPerformanceFrequency(&value);
    return value.QuadPart > 0 ? value.QuadPart : 1;
}

constexpr uint64_t FileTimeValue(const FILETIME& ft) noexcept {
    return (static_cast<uint64_t>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
}

double ClampPercent(double value) noexcept {
    return std::clamp(value, 0.0, 100.0);
}

double SampleCpu(VnSystemMonitorState& state) noexcept {
    const int64_t now = QueryCounter();
    if (state.last_cpu_qpc > 0) {
        const double elapsed_ms = static_cast<double>(now - state.last_cpu_qpc) * 1000.0 / static_cast<double>(QueryFrequency());
        if (elapsed_ms < kMinCpuSampleMs) return ClampPercent(state.smoothed_cpu);
    }

    FILETIME idle_ft{}, kernel_ft{}, user_ft{};
    if (!GetSystemTimes(&idle_ft, &kernel_ft, &user_ft)) return ClampPercent(state.smoothed_cpu);

    const uint64_t idle = FileTimeValue(idle_ft);
    const uint64_t kernel = FileTimeValue(kernel_ft);
    const uint64_t user = FileTimeValue(user_ft);

    if (state.last_idle > 0) {
        const uint64_t delta_idle = idle > state.last_idle ? idle - state.last_idle : 0;
        const uint64_t delta_kernel = kernel > state.last_kernel ? kernel - state.last_kernel : 0;
        const uint64_t delta_user = user > state.last_user ? user - state.last_user : 0;
        const uint64_t delta_total = delta_kernel + delta_user;
        if (delta_total > 0) {
            const double busy = delta_total > delta_idle ? static_cast<double>(delta_total - delta_idle) : 0.0;
            const double raw = ClampPercent(busy / static_cast<double>(delta_total) * 100.0);
            state.smoothed_cpu = state.has_cpu_measurement
                ? state.smoothed_cpu * kCpuSmoothing + raw * (1.0 - kCpuSmoothing)
                : raw;
            state.has_cpu_measurement = 1;
        }
    }

    state.last_idle = idle;
    state.last_kernel = kernel;
    state.last_user = user;
    state.last_cpu_qpc = now;
    return ClampPercent(state.smoothed_cpu);
}

struct NetworkTotals {
    uint64_t received;
    uint64_t sent;
    bool any_up;
};

// Counts every up interface except loopback and tunnels, matching the old NetworkInterface filter.
bool ReadNetworkTotals(NetworkTotals& totals) noexcept {
    totals = {};
    MIB_IF_TABLE2* table = nullptr;
    if (GetIfTable2(&table) != NO_ERROR || table == nullptr) return false;
    for (ULONG i = 0; i < table->NumEntries; ++i) {
        const MIB_IF_ROW2& row = table->Table[i];
        if (row.OperStatus != IfOperStatusUp) continue;
        if (row.Type == kIfTypeSoftwareLoopback || row.Type == kIfTypeTunnel) continue;
        totals.any_up = true;
        totals.received += row.InOctets;
        totals.sent += row.OutOctets;
    }
    FreeMibTable(table);
    return true;
}

int32_t SampleNetwork(VnSystemMonitorState& state) noexcept {
    const int64_t now = QueryCounter();
    const double frequency = static_cast<double>(QueryFrequency());
    const int64_t last = state.last_net_qpc;
    if (last > 0 && static_cast<double>(now - last) / frequency <= kMinNetSampleSeconds) return VN_OK;

    NetworkTotals totals{};
    if (!ReadNetworkTotals(totals)) {
        state.down_bytes_per_sec = 0.0;
        state.up_bytes_per_sec = 0.0;
        return VN_ERR_FAIL;
    }

    if (!totals.any_up) {
        state.down_bytes_per_sec = 0.0;
        state.up_bytes_per_sec = 0.0;
        state.last_recv_bytes = 0;
        state.last_sent_bytes = 0;
        state.last_net_qpc = now;
        return VN_OK;
    }

    if (last > 0) {
        const double elapsed = static_cast<double>(now - last) / frequency;
        if (elapsed > kMinNetSampleSeconds) {
            const uint64_t delta_recv = totals.received > state.last_recv_bytes ? totals.received - state.last_recv_bytes : 0;
            const uint64_t delta_sent = totals.sent > state.last_sent_bytes ? totals.sent - state.last_sent_bytes : 0;
            state.down_bytes_per_sec = static_cast<double>(delta_recv) / elapsed;
            state.up_bytes_per_sec = static_cast<double>(delta_sent) / elapsed;
        }
    }

    state.last_recv_bytes = totals.received;
    state.last_sent_bytes = totals.sent;
    state.last_net_qpc = now;
    return VN_OK;
}

} // namespace
} // namespace vnotch::modules

extern "C" {

VN_EXPORT int32_t __stdcall vn_sysmon_reset(vnotch::modules::VnSystemMonitorState* state) noexcept {
    if (!state) return VN_ERR_INVALID_ARG;
    *state = {};
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_sysmon_sample_cpu(
    vnotch::modules::VnSystemMonitorState* state, double* out_cpu_percent) noexcept {
    if (!state || !out_cpu_percent) return VN_ERR_INVALID_ARG;
    *out_cpu_percent = vnotch::modules::SampleCpu(*state);
    return VN_OK;
}

// Updates state.down_bytes_per_sec / up_bytes_per_sec; the previous rates stay when called too soon.
VN_EXPORT int32_t __stdcall vn_sysmon_sample_network(
    vnotch::modules::VnSystemMonitorState* state, double* out_down_bytes_per_sec, double* out_up_bytes_per_sec) noexcept {
    if (!state || !out_down_bytes_per_sec || !out_up_bytes_per_sec) return VN_ERR_INVALID_ARG;
    const int32_t status = vnotch::modules::SampleNetwork(*state);
    *out_down_bytes_per_sec = state->down_bytes_per_sec;
    *out_up_bytes_per_sec = state->up_bytes_per_sec;
    return status;
}

VN_EXPORT int32_t __stdcall vn_sysmon_read_memory(
    int32_t include_installed, vnotch::modules::VnSystemMemory* out_memory) noexcept {
    if (!out_memory) return VN_ERR_INVALID_ARG;
    *out_memory = {};
    MEMORYSTATUSEX status{};
    status.dwLength = sizeof(status);
    if (!GlobalMemoryStatusEx(&status)) return VN_ERR_FAIL;
    out_memory->total_physical = status.ullTotalPhys;
    out_memory->available_physical = status.ullAvailPhys;
    out_memory->memory_load_percent = std::min<uint32_t>(status.dwMemoryLoad, 100u);
    if (include_installed != 0) {
        ULONGLONG installed_kb = 0;
        if (GetPhysicallyInstalledSystemMemory(&installed_kb) && installed_kb > 0)
            out_memory->installed_physical = static_cast<uint64_t>(installed_kb) * 1024ull;
    }
    return VN_OK;
}

} // extern "C"
