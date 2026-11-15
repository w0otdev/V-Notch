#include "vnotch_common.h"
#include "vnotch_cpu.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <immintrin.h>
#include <limits>
#include <string_view>


extern "C" {
VN_EXPORT int32_t __stdcall vn_progress_interpolate_ticks(
    int64_t last_position_ticks,
    int64_t duration_ticks,
    int64_t elapsed_ticks,
    double playback_rate,
    uint8_t is_playing,
    uint8_t is_throttled,
    uint8_t is_web_or_youtube,
    int64_t* out_display_ticks,
    double* out_ratio) noexcept
{
    if (!out_display_ticks || !out_ratio)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (duration_ticks <= 0)
    {
        *out_display_ticks = 0;
        *out_ratio = 0.0;
        return VN_OK;
    }

    int64_t display = last_position_ticks;
    if (is_playing != 0)
    {
        constexpr int64_t kTicksPerSecond = 10000000LL;
        int64_t cap_seconds = (is_throttled != 0)
            ? 3600LL
            : ((is_web_or_youtube != 0) ? 600LL : 30LL);
        int64_t max_elapsed = cap_seconds * kTicksPerSecond;
        int64_t clamped_elapsed = std::min(elapsed_ticks, max_elapsed);
        if (clamped_elapsed > 0)
        {
            const double rate = (std::isfinite(playback_rate) && playback_rate >= 0.0) ? std::min(playback_rate, 16.0) : 1.0;
            display += static_cast<int64_t>(static_cast<double>(clamped_elapsed) * rate);
        }
    }

    display = std::clamp<int64_t>(display, 0LL, duration_ticks);
    *out_display_ticks = display;
    *out_ratio = static_cast<double>(display) / static_cast<double>(duration_ticks);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_progress_format_time(
    int64_t time_ticks,
    wchar_t* out_buf,
    int32_t buf_cap,
    int32_t* out_len) noexcept
{
    if (!out_buf || buf_cap <= 0 || !out_len)
    {
        return VN_ERR_INVALID_ARG;
    }

    constexpr int64_t kTicksPerSecond = 10000000LL;
    constexpr int64_t kTicksPerMinute = 60LL * kTicksPerSecond;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;

    int64_t ticks = std::clamp<int64_t>(time_ticks, 0LL, 99999LL * kTicksPerHour);
    int32_t written = 0;

    if (ticks >= kTicksPerHour)
    {
        int32_t hours = static_cast<int32_t>(ticks / kTicksPerHour);
        int32_t minutes = static_cast<int32_t>((ticks % kTicksPerHour) / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%d:%02d:%02d", hours, minutes, seconds);
    }
    else
    {
        int32_t minutes = static_cast<int32_t>(ticks / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%d:%02d", minutes, seconds);
    }

    *out_len = (written > 0) ? written : 0;
    return VN_OK;
}


}
