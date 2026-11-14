#include "../vnotch_native.h"

namespace
{
    constexpr int64_t kTicksPerMinute = 600'000'000LL;
    constexpr int64_t kTicksPerHour = 36'000'000'000LL;
    constexpr int64_t kTicksPerDay = 864'000'000'000LL;
    constexpr int64_t kStaleThresholdTicks = 10LL * kTicksPerMinute;
    constexpr int64_t kFutureSkewThresholdTicks = 1LL * kTicksPerMinute;

    constexpr int32_t kAiUsageStateReady = 3;
    constexpr int32_t kAiUsageStateLoading = 2;
    constexpr int32_t kAiUsageStateWaiting = 7;
    constexpr int32_t kAiUsageStateUnavailable = 8;

    [[nodiscard]] constexpr uint8_t ComputeIsStale(
        uint8_t has_updated_at,
        int64_t updated_at_utc_ticks,
        int64_t now_utc_ticks) noexcept
    {
        if (has_updated_at == 0)
        {
            return 1;
        }
        if ((now_utc_ticks - updated_at_utc_ticks) > kStaleThresholdTicks)
        {
            return 1;
        }
        if (updated_at_utc_ticks > (now_utc_ticks + kFutureSkewThresholdTicks))
        {
            return 1;
        }
        return 0;
    }
}

VN_EXPORT int32_t __stdcall vn_ai_usage_compute_window_visual(
    double used_percent,
    int32_t duration_minutes,
    uint8_t has_resets_at,
    int64_t resets_at_utc_ticks,
    uint8_t has_updated_at,
    int64_t updated_at_utc_ticks,
    int64_t now_utc_ticks,
    VnAiQuotaWindowVisualOut* out_visual) noexcept
{
    if (out_visual == nullptr)
    {
        return -1;
    }

    const uint8_t is_expired = (has_resets_at != 0 && resets_at_utc_ticks <= now_utc_ticks) ? 1 : 0;
    const uint8_t is_stale = ComputeIsStale(has_updated_at, updated_at_utc_ticks, now_utc_ticks);

    double sanitized_used = 0.0;
    if (is_expired == 0 && std::isfinite(used_percent))
    {
        sanitized_used = std::clamp(used_percent, 0.0, 100.0);
    }

    uint8_t severity_tier = 0;
    if (sanitized_used >= 90.0)
    {
        severity_tier = 2;
    }
    else if (sanitized_used >= 70.0)
    {
        severity_tier = 1;
    }

    uint8_t reset_mode = 0;
    int32_t reset_hours = 0;
    int32_t reset_minutes = 0;

    if (has_resets_at != 0)
    {
        if (resets_at_utc_ticks <= now_utc_ticks)
        {
            reset_mode = 1;
        }
        else
        {
            const int64_t remaining_ticks = resets_at_utc_ticks - now_utc_ticks;
            if (remaining_ticks < kTicksPerDay && duration_minutes != 10080)
            {
                const int64_t total_minutes = (remaining_ticks + kTicksPerMinute - 1LL) / kTicksPerMinute;
                const int32_t clamped_minutes = static_cast<int32_t>(std::clamp<int64_t>(total_minutes, 1LL, 1440LL));
                if (clamped_minutes < 60)
                {
                    reset_mode = 2;
                    reset_minutes = clamped_minutes;
                }
                else
                {
                    reset_mode = 3;
                    reset_hours = clamped_minutes / 60;
                    reset_minutes = clamped_minutes % 60;
                }
            }
            else
            {
                reset_mode = 4;
            }
        }
    }

    out_visual->clamped_used_percent = sanitized_used;
    out_visual->clamped_remaining_percent = std::clamp(100.0 - sanitized_used, 0.0, 100.0);
    out_visual->row_opacity = (is_expired != 0 || is_stale != 0) ? 0.55 : 1.0;
    out_visual->is_expired = is_expired;
    out_visual->is_stale = is_stale;
    out_visual->severity_tier = severity_tier;
    out_visual->reset_mode = reset_mode;
    out_visual->reset_hours = reset_hours;
    out_visual->reset_minutes = reset_minutes;
    return 0;
}

VN_EXPORT int32_t __stdcall vn_ai_usage_compute_card_summary(
    int32_t state,
    int32_t window_count,
    uint8_t has_updated_at,
    int64_t updated_at_utc_ticks,
    int64_t now_utc_ticks,
    VnAiProviderCardSummaryOut* out_summary) noexcept
{
    if (out_summary == nullptr)
    {
        return -1;
    }

    const uint8_t show_rows = (state == kAiUsageStateReady && window_count > 0) ? 1 : 0;
    const int32_t visible_count = (show_rows != 0) ? std::clamp(window_count, 0, 2) : 0;
    const uint8_t is_stale = ComputeIsStale(has_updated_at, updated_at_utc_ticks, now_utc_ticks);

    uint8_t action_kind = 1;
    if (state == kAiUsageStateReady ||
        state == kAiUsageStateLoading ||
        state == kAiUsageStateWaiting ||
        state == kAiUsageStateUnavailable)
    {
        action_kind = 0;
    }

    out_summary->show_quota_rows = show_rows;
    out_summary->visible_window_count = visible_count;
    out_summary->is_stale = is_stale;
    out_summary->action_kind = action_kind;
    return 0;
}

VN_EXPORT int32_t __stdcall vn_ai_usage_resolve_claude_setup_state(
    int32_t usage_state,
    uint8_t has_settings_file,
    uint8_t has_status_line,
    uint8_t matches_bridge_command,
    uint8_t has_custom_api_endpoint,
    uint8_t* out_action_kind,
    int32_t* out_prompt_mode) noexcept
{
    if (out_action_kind == nullptr || out_prompt_mode == nullptr)
    {
        return -1;
    }

    if (usage_state != kAiUsageStateWaiting)
    {
        *out_prompt_mode = 0;
        *out_action_kind = (usage_state == kAiUsageStateReady ||
                            usage_state == kAiUsageStateLoading ||
                            usage_state == kAiUsageStateUnavailable)
                               ? 0
                               : 1;
        return 0;
    }

    if (has_custom_api_endpoint != 0)
    {
        *out_action_kind = 1;
        *out_prompt_mode = 2;
        return 0;
    }

    if (matches_bridge_command == 0)
    {
        *out_action_kind = 1;
        *out_prompt_mode = (has_settings_file != 0 && has_status_line != 0) ? 3 : 1;
        return 0;
    }

    *out_action_kind = 0;
    *out_prompt_mode = 0;
    return 0;
}
