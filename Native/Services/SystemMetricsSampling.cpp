#include "vnotch_native.h"
#include <pdh.h>
#include <pdhmsg.h>
#include <psapi.h>
#include <cwchar>

#pragma comment(lib, "pdh.lib")
#pragma comment(lib, "psapi.lib")

namespace {
uint64_t ticks(FILETIME value) noexcept {
    return (uint64_t{value.dwHighDateTime} << 32) | value.dwLowDateTime;
}

// Each dedicated sampler thread owns its query and reusable, suitably aligned buffer.
struct GpuQuery {
    PDH_HQUERY query{};
    PDH_HCOUNTER counter{};
    std::vector<uint64_t> buffer;
    ULONGLONG next_retry{};
    ~GpuQuery() { reset(); }
    void reset() noexcept {
        if (query) PdhCloseQuery(query);
        query = nullptr;
        counter = nullptr;
        next_retry = 0;
    }
    void sample(VnSystemMetricsSnapshot& result) {
        if (!query) {
            const auto now = GetTickCount64();
            if (now < next_retry) return;
            if (PdhOpenQueryW(nullptr, 0, &query) != ERROR_SUCCESS ||
                PdhAddEnglishCounterW(query, L"\\GPU Engine(*)\\Utilization Percentage", 0, &counter) != ERROR_SUCCESS) {
                reset();
                next_retry = now + 60000;
                return;
            }
            // Rate counters require two collections. Publish zero for the first one.
            PdhCollectQueryData(query);
            return;
        }
        const auto collection = PdhCollectQueryData(query);
        if (collection != ERROR_SUCCESS) {
            if (collection != PDH_NO_DATA) {
                reset();
                next_retry = GetTickCount64() + 5000;
            }
            return;
        }
        constexpr DWORD format = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;
        DWORD count = 0;
        DWORD bytes = static_cast<DWORD>(buffer.size() * sizeof(uint64_t));
        auto status = bytes ? PdhGetFormattedCounterArrayW(counter, format, &bytes, &count,
            reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data())) : PDH_MORE_DATA;
        if (status == PDH_MORE_DATA) {
            // The size from a failed nonzero-buffer call is not reliable (PDH contract).
            bytes = 0;
            status = PdhGetFormattedCounterArrayW(counter, format, &bytes, &count, nullptr);
            if (status != PDH_MORE_DATA || bytes == 0 || bytes > 64 * 1024 * 1024) return;
            buffer.resize((bytes + sizeof(uint64_t) - 1) / sizeof(uint64_t));
            bytes = static_cast<DWORD>(buffer.size() * sizeof(uint64_t));
            status = PdhGetFormattedCounterArrayW(counter, format, &bytes, &count,
                reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data()));
        }
        if (status != ERROR_SUCCESS) return;
        wchar_t prefix[32]{};
        swprintf_s(prefix, L"pid_%lu_", GetCurrentProcessId());
        const auto prefix_length = wcslen(prefix);
        const auto items = reinterpret_cast<const PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data());
        for (DWORD i = 0; i < count; ++i) {
            const auto& item = items[i];
            if (!item.szName || (item.FmtValue.CStatus != PDH_CSTATUS_VALID_DATA &&
                item.FmtValue.CStatus != PDH_CSTATUS_NEW_DATA)) continue;
            const std::wstring_view name(item.szName);
            if (name.find(L"engtype_3D") == name.npos && name.find(L"engtype_Compute") == name.npos &&
                name.find(L"engtype_VR") == name.npos) continue;
            const double value = item.FmtValue.doubleValue;
            if (!std::isfinite(value) || value < 0) continue;
            result.global_gpu += value;
            if (name.starts_with(std::wstring_view(prefix, prefix_length))) result.process_gpu += value;
        }
        result.process_gpu = std::clamp(result.process_gpu, 0.0, 100.0);
        result.global_gpu = std::clamp(result.global_gpu, 0.0, 100.0);
    }
};
thread_local GpuQuery gpu_query;
}

VN_EXPORT int32_t __stdcall vn_gpu_monitor_sample(VnSystemMetricsSnapshot* output) noexcept {
    if (!output) return VN_ERR_INVALID_ARG;
    *output = {};
    try { gpu_query.sample(*output); return VN_OK; }
    catch (...) { gpu_query.reset(); return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_gpu_monitor_reset() noexcept {
    gpu_query.reset();
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_system_metrics_sample(VnSystemMetricsState* state, VnSystemMetricsSnapshot* output) noexcept {
    if (!state || !output) return VN_ERR_INVALID_ARG;
    *output = {};
    FILETIME creation{}, exit{}, kernel{}, user{}, idle{};
    LARGE_INTEGER now{}, frequency{};
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&frequency);
    if (GetProcessTimes(GetCurrentProcess(), &creation, &exit, &kernel, &user)) {
        const auto total = ticks(kernel) + ticks(user);
        const double seconds = frequency.QuadPart > 0 ?
            static_cast<double>(now.QuadPart - state->timestamp) / frequency.QuadPart : 0;
        if (state->timestamp && seconds > 0.005 && total >= state->process_time) {
            const auto processors = std::max<DWORD>(1, GetActiveProcessorCount(ALL_PROCESSOR_GROUPS));
            const double raw = std::clamp((total - state->process_time) / 100000.0 / seconds / processors, 0.0, 100.0);
            state->process_cpu = state->process_cpu * 0.88 + raw * 0.12;
        }
        state->timestamp = now.QuadPart;
        state->process_time = total;
        output->process_cpu = state->process_cpu < 0.05 ? 0 : state->process_cpu;
    }
    if (GetSystemTimes(&idle, &kernel, &user)) {
        const auto i = ticks(idle), k = ticks(kernel), u = ticks(user);
        if (state->idle_time && i >= state->idle_time && k >= state->kernel_time && u >= state->user_time) {
            const auto total = k - state->kernel_time + u - state->user_time;
            const auto inactive = i - state->idle_time;
            if (total) state->global_cpu = state->global_cpu * 0.88 +
                std::clamp(100.0 * (total > inactive ? total - inactive : 0) / total, 0.0, 100.0) * 0.12;
        }
        state->idle_time = i; state->kernel_time = k; state->user_time = u;
        output->global_cpu = state->global_cpu;
    }
    PROCESS_MEMORY_COUNTERS memory{};
    memory.cb = sizeof(memory);
    if (GetProcessMemoryInfo(GetCurrentProcess(), &memory, sizeof(memory))) output->working_set = memory.WorkingSetSize;
    MEMORYSTATUSEX global{};
    global.dwLength = sizeof(global);
    if (GlobalMemoryStatusEx(&global)) {
        output->ram_total = global.ullTotalPhys;
        output->ram_used = global.ullTotalPhys - global.ullAvailPhys;
        output->ram_percent = global.dwMemoryLoad;
    }
    return VN_OK;
}
