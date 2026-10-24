#include "vnotch_common.h"
#include <cstdio>

namespace vnotch::services
{

class CrashReporter final
{
public:
    static constexpr int64_t kMaxCrashLogSizeBytes = 2LL * 1024LL * 1024LL;

    static bool ShouldRotateLog(int64_t current_size_bytes) noexcept
    {
        return current_size_bytes > kMaxCrashLogSizeBytes;
    }

    static std::u16string FormatDuration(
        double total_days,
        double total_hours,
        double total_minutes,
        double total_seconds,
        int32_t hours,
        int32_t minutes,
        int32_t seconds)
    {
        char buf[64];
        int n = 0;
        if (total_days >= 1.0)
        {
            n = std::snprintf(
                buf, sizeof(buf), "%dd %dh %dm %ds",
                static_cast<int>(total_days), static_cast<int>(hours), static_cast<int>(minutes), static_cast<int>(seconds));
        }
        else if (total_hours >= 1.0)
        {
            n = std::snprintf(
                buf, sizeof(buf), "%dh %dm %ds",
                static_cast<int>(total_hours), static_cast<int>(minutes), static_cast<int>(seconds));
        }
        else if (total_minutes >= 1.0)
        {
            n = std::snprintf(
                buf, sizeof(buf), "%dm %ds",
                static_cast<int>(total_minutes), static_cast<int>(seconds));
        }
        else
        {
            n = std::snprintf(buf, sizeof(buf), "%.1fs", total_seconds);
        }

        std::u16string out;
        if (n > 0)
        {
            out.reserve(static_cast<size_t>(n));
            for (int i = 0; i < n; ++i)
            {
                out.push_back(static_cast<char16_t>(buf[i]));
            }
        }
        return out;
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
    std::u16string formatted = vnotch::services::CrashReporter::FormatDuration(
        total_days, total_hours, total_minutes, total_seconds, hours, minutes, seconds);
    return vnotch::copy_out_u16(formatted, out_buf, out_cap, out_len);
}
