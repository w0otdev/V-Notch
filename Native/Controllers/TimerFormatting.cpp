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

#include "Parsing/AsciiNumber.h"
namespace vnotch::controllers { namespace {
constexpr double UnitSecondsMultiplier(wchar_t unit) noexcept
{
    switch (unit)
    {
    case L's': return 1.0;
    case L'm': return 60.0;
    case L'h': return 3600.0;
    case L'd': return 86400.0;
    default: return 0.0;
    }
}

bool TryParseColonDuration(std::wstring_view s, int64_t* out_ticks) noexcept
{
    std::wstring_view parts[4]{};
    int32_t part_count = 0;
    size_t pos = 0;
    while (pos <= s.size())
    {
        if (part_count >= 4)
        {
            return false;
        }
        const size_t colon = s.find(L':', pos);
        if (colon == std::wstring_view::npos)
        {
            parts[part_count++] = s.substr(pos);
            break;
        }
        parts[part_count++] = s.substr(pos, colon - pos);
        pos = colon + 1;
    }

    if (part_count == 2)
    {
        int32_t mins = 0, secs = 0;
        if (!TryParseInt32Strict(parts[0], &mins) || !TryParseInt32Strict(parts[1], &secs) ||
            mins < 0 || secs < 0 || secs >= 60)
        {
            return false;
        }
        return TryTicksFromSeconds(static_cast<double>(mins) * 60.0 + static_cast<double>(secs), out_ticks);
    }

    if (part_count == 3)
    {
        int32_t hrs = 0, mins = 0, secs = 0;
        if (!TryParseInt32Strict(parts[0], &hrs) || !TryParseInt32Strict(parts[1], &mins) ||
            !TryParseInt32Strict(parts[2], &secs) ||
            hrs < 0 || mins < 0 || mins >= 60 || secs < 0 || secs >= 60)
        {
            return false;
        }
        const double total_sec = static_cast<double>(hrs) * 3600.0 +
                                 static_cast<double>(mins) * 60.0 +
                                 static_cast<double>(secs);
        return TryTicksFromSeconds(total_sec, out_ticks);
    }

    return false;
}

bool TryParseUnitDuration(std::wstring_view s, int64_t* out_ticks) noexcept
{
    double total_seconds = 0.0;
    size_t i = 0;
    while (i < s.size())
    {
        while (i < s.size() && IsAsciiWhitespace(s[i]))
        {
            ++i;
        }
        if (i >= s.size())
        {
            break;
        }
        const size_t num_start = i;
        while (i < s.size() && s[i] >= L'0' && s[i] <= L'9')
        {
            ++i;
        }
        if (i < s.size() && s[i] == L'.')
        {
            ++i;
            const size_t frac_start = i;
            while (i < s.size() && s[i] >= L'0' && s[i] <= L'9')
            {
                ++i;
            }
            if (i == frac_start)
            {
                return false;
            }
        }
        const std::wstring_view num_sv = s.substr(num_start, i - num_start);
        while (i < s.size() && IsAsciiWhitespace(s[i]))
        {
            ++i;
        }
        if (i >= s.size())
        {
            return false;
        }
        const double mult = UnitSecondsMultiplier(ToLowerAscii(s[i++]));
        double val = 0.0;
        if (mult == 0.0 || !TryParseDoubleStrict(num_sv, &val))
        {
            return false;
        }
        total_seconds += val * mult;
    }
    return TryTicksFromSeconds(total_seconds, out_ticks);
}


}}

extern "C" {
VN_EXPORT int32_t __stdcall vn_timer_parse_custom_time(
    const wchar_t* input,
    int32_t input_len,
    int64_t* out_ticks,
    uint8_t* out_valid) noexcept
{
    if (!out_ticks || !out_valid)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_ticks = 0;
    *out_valid = 0;

    if (!input || input_len <= 0)
    {
        return VN_OK;
    }

    std::wstring_view s = vnotch::controllers::TrimWhitespace(
        std::wstring_view(input, static_cast<size_t>(input_len)));
    if (s.empty())
    {
        return VN_OK;
    }

    if (s.find(L':') != std::wstring_view::npos)
    {
        if (vnotch::controllers::TryParseColonDuration(s, out_ticks))
        {
            *out_valid = 1;
        }
        return VN_OK;
    }

    double plain_minutes = 0.0;
    if (vnotch::controllers::TryParseDoubleStrict(s, &plain_minutes))
    {
        if (vnotch::controllers::TryTicksFromSeconds(plain_minutes * 60.0, out_ticks))
        {
            *out_valid = 1;
        }
        return VN_OK;
    }

    if (vnotch::controllers::TryParseUnitDuration(s, out_ticks))
    {
        *out_valid = 1;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_format_remaining(
    int64_t remaining_ticks,
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
    constexpr int64_t kTicksPerDay = 24LL * kTicksPerHour;

    int64_t ticks = std::clamp<int64_t>(remaining_ticks, 0LL, 99999LL * kTicksPerHour);
    int32_t written = 0;

    if (ticks >= kTicksPerDay)
    {
        int32_t total_days = static_cast<int32_t>(ticks / kTicksPerDay);
        int32_t hours = static_cast<int32_t>((ticks % kTicksPerDay) / kTicksPerHour);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%dd %02dh", total_days, hours);
    }
    else if (ticks >= kTicksPerHour)
    {
        int32_t total_hours = static_cast<int32_t>(ticks / kTicksPerHour);
        int32_t minutes = static_cast<int32_t>((ticks % kTicksPerHour) / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%02d:%02d:%02d", total_hours, minutes, seconds);
    }
    else
    {
        int32_t total_minutes = static_cast<int32_t>(ticks / kTicksPerMinute);
        int32_t seconds = static_cast<int32_t>((ticks % kTicksPerMinute) / kTicksPerSecond);
        written = std::swprintf(out_buf, static_cast<size_t>(buf_cap), L"%02d:%02d", total_minutes, seconds);
    }

    *out_len = (written > 0) ? written : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_compute_adjust_ticks(
    int64_t current_duration_ticks,
    int32_t direction,
    int64_t* out_next_ticks,
    uint8_t* out_changed) noexcept
{
    if (!out_next_ticks || !out_changed)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (direction == 0)
    {
        *out_next_ticks = current_duration_ticks;
        *out_changed = 0;
        return VN_OK;
    }

    constexpr int64_t kTicksPerMinute = 60LL * 10000000LL;
    constexpr int64_t kTicksPerHour = 60LL * kTicksPerMinute;
    constexpr int64_t kTicksPerDay = 24LL * kTicksPerHour;
    constexpr int64_t kMinimumTicks = kTicksPerMinute;
    constexpr int64_t kMaximumTicks = 7LL * kTicksPerDay;

    int64_t clamped_current = std::clamp<int64_t>(current_duration_ticks, 0LL, kMaximumTicks);
    int64_t step = (clamped_current >= kTicksPerDay)
        ? kTicksPerHour
        : ((clamped_current >= kTicksPerHour) ? (5LL * kTicksPerMinute) : kTicksPerMinute);

    int64_t next = clamped_current + (direction > 0 ? step : -step);
    next = std::clamp(next, kMinimumTicks, kMaximumTicks);
    *out_next_ticks = next;
    *out_changed = (next != current_duration_ticks) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_timer_compute_progress(
    int64_t duration_ticks,
    int64_t remaining_ticks,
    double* out_progress) noexcept
{
    if (!out_progress)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (duration_ticks <= 0)
    {
        *out_progress = 0.0;
        return VN_OK;
    }
    double ratio = static_cast<double>(remaining_ticks) / static_cast<double>(duration_ticks);
    *out_progress = 1.0 - std::clamp(ratio, 0.0, 1.0);
    return VN_OK;
}


}
