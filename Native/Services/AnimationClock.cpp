#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_anim_clock_try_advance(
    VnCoreAnimClockState* io_state,
    int64_t now_tick,
    int64_t stopwatch_freq,
    double target_fps,
    double* out_seconds,
    uint8_t* out_advanced) noexcept
{
    if (!io_state || !out_seconds || !out_advanced || stopwatch_freq <= 0)
        return VN_ERR_INVALID_ARG;

    *out_seconds = 0.0;
    *out_advanced = 0;

    target_fps = std::isfinite(target_fps) ? std::max(1.0, target_fps) : 60.0;
    const double freq = static_cast<double>(stopwatch_freq);
    const double interval = freq / target_fps;

    if (io_state->target_fps != target_fps)
    {
        io_state->target_fps = target_fps;
        io_state->next_tick = static_cast<double>(io_state->last_tick) + interval;
    }

    if (now_tick <= io_state->last_tick || static_cast<double>(now_tick) + freq * 0.0001 < io_state->next_tick)
        return VN_OK;

    const double elapsed = static_cast<double>(now_tick - io_state->last_tick) / freq;
    *out_seconds = std::clamp(elapsed, 0.0, 0.05);
    io_state->last_tick = now_tick;
    io_state->next_tick += std::max(1.0, std::floor((static_cast<double>(now_tick) - io_state->next_tick) / interval) + 1.0) * interval;
    *out_advanced = 1;
    return VN_OK;
}

