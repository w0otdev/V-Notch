#include "vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdio>

namespace vnotch::services
{

class CrashReporter final
{
    static int SafeDoubleToInt(double v) noexcept
    {
        if (!std::isfinite(v)) return 0;
        return static_cast<int>(std::clamp(v, -2147483647.0, 2147483647.0));
    }

public:
    static constexpr int64_t kMaxCrashLogSizeBytes = 2LL * 1024LL * 1024LL;

    static bool ShouldRotateLog(int64_t current_size_bytes) noexcept
    {
        return current_size_bytes > kMaxCrashLogSizeBytes;
    }

    static size_t FormatDuration(
        double total_days,
        double total_hours,
        double total_minutes,
        double total_seconds,
        int32_t hours,
        int32_t minutes,
        int32_t seconds,
        char16_t (&out)[64]) noexcept
    {
        char buf[64]{};
        int n = 0;
        if (std::isfinite(total_days) && total_days >= 1.0)
        {
            n = std::snprintf(
                buf, sizeof(buf), "%dd %dh %dm %ds",
                SafeDoubleToInt(total_days), static_cast<int>(hours), static_cast<int>(minutes), static_cast<int>(seconds));
        }
        else if (std::isfinite(total_hours) && total_hours >= 1.0)
        {
            n = std::snprintf(
                buf, sizeof(buf), "%dh %dm %ds",
                SafeDoubleToInt(total_hours), static_cast<int>(minutes), static_cast<int>(seconds));
        }
        else if (std::isfinite(total_minutes) && total_minutes >= 1.0)
        {
            n = std::snprintf(
                buf, sizeof(buf), "%dm %ds",
                SafeDoubleToInt(total_minutes), static_cast<int>(seconds));
        }
        else
        {
            double safe_sec = std::isfinite(total_seconds) ? std::clamp(total_seconds, -1e9, 1e9) : 0.0;
            n = std::snprintf(buf, sizeof(buf), "%.1fs", safe_sec);
        }

        if (n <= 0)
        {
            return 0;
        }
        const int clamped_n = std::min<int>(n, static_cast<int>(sizeof(buf) - 1));
        for (int i = 0; i < clamped_n; ++i)
        {
            out[i] = static_cast<char16_t>(static_cast<unsigned char>(buf[i]));
        }
        return static_cast<size_t>(clamped_n);
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_crash_reporter_should_rotate(
    int64_t current_size_bytes,
    uint8_t* out_rotate) noexcept
{
    if (out_rotate == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_rotate = vnotch::services::CrashReporter::ShouldRotateLog(current_size_bytes) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_crash_reporter_format_duration(
    double total_days,
    double total_hours,
    double total_minutes,
    double total_seconds,
    int32_t hours,
    int32_t minutes,
    int32_t seconds,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    char16_t formatted[64]{};
    size_t len = vnotch::services::CrashReporter::FormatDuration(
        total_days, total_hours, total_minutes, total_seconds, hours, minutes, seconds, formatted);
    return vnotch::copy_out_u16(std::u16string_view(formatted, len), out_buf, out_cap, out_len);
}
