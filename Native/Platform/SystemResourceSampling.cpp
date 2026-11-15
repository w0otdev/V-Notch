#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_perf_diag_sample_thread_and_handle_counts(
    int64_t now_ticks,
    int64_t* io_last_thread_ticks,
    int32_t* io_cached_thread_count,
    int64_t stopwatch_freq,
    int32_t* out_thread_count,
    int32_t* out_handle_count) noexcept
{
    if (!io_last_thread_ticks || !io_cached_thread_count ||
        !out_thread_count || !out_handle_count || stopwatch_freq <= 0)
    {
        return VN_ERR_INVALID_ARG;
    }

    const bool refresh_threads =
        (*io_last_thread_ticks == 0) ||
        (static_cast<double>(now_ticks - *io_last_thread_ticks) / static_cast<double>(stopwatch_freq) >= 2.0);

    if (refresh_threads)
    {
        const HANDLE snap = ::CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        if (snap != INVALID_HANDLE_VALUE)
        {
            const DWORD pid = ::GetCurrentProcessId();
            THREADENTRY32 te{};
            te.dwSize = sizeof(THREADENTRY32);
            int32_t count = 0;
            if (::Thread32First(snap, &te))
            {
                do
                {
                    if (te.dwSize >= FIELD_OFFSET(THREADENTRY32, th32OwnerProcessID) + sizeof(DWORD) &&
                        te.th32OwnerProcessID == pid)
                    {
                        ++count;
                    }
                    te.dwSize = sizeof(THREADENTRY32);
                } while (::Thread32Next(snap, &te));
            }
            ::CloseHandle(snap);
            if (count > 0)
                *io_cached_thread_count = count;
        }
        *io_last_thread_ticks = now_ticks;
    }

    *out_thread_count = *io_cached_thread_count;

    DWORD h_count = 0;
    *out_handle_count = ::GetProcessHandleCount(::GetCurrentProcess(), &h_count)
        ? static_cast<int32_t>(h_count)
        : 0;

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_perf_diag_sample_global_memory(
    uint64_t* out_ram_used,
    uint64_t* out_ram_total,
    uint64_t* out_ram_avail,
    double* out_ram_percent) noexcept
{
    if (!out_ram_used || !out_ram_total || !out_ram_avail || !out_ram_percent)
        return VN_ERR_INVALID_ARG;

    *out_ram_used = 0;
    *out_ram_total = 0;
    *out_ram_avail = 0;
    *out_ram_percent = 0.0;

    MEMORYSTATUSEX ms{};
    ms.dwLength = sizeof(MEMORYSTATUSEX);
    if (!::GlobalMemoryStatusEx(&ms))
        return VN_ERR_FAIL;

    *out_ram_total = ms.ullTotalPhys;
    *out_ram_avail = ms.ullAvailPhys;
    *out_ram_used = (ms.ullTotalPhys > ms.ullAvailPhys) ? (ms.ullTotalPhys - ms.ullAvailPhys) : 0ULL;
    *out_ram_percent = std::clamp(static_cast<double>(ms.dwMemoryLoad), 0.0, 100.0);
    return VN_OK;
}

