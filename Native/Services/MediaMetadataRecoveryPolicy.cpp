#include "vnotch_common.h"

namespace vnotch::services
{
    class MediaMetadataRecoveryPolicy final
    {
    public:
        static int32_t SelectDetectionMode(bool is_any_playing, std::u16string_view cur_track, bool is_throttled, bool is_session_gap_recovery) noexcept
        {
            bool empty_track = is_null_or_whitespace(cur_track);
            if (is_session_gap_recovery || (is_any_playing && empty_track))
            {
                return 1; // AwaitingMetadata
            }
            if (!is_any_playing || empty_track)
            {
                return 0; // Idle
            }
            return is_throttled ? 3 : 2;
        }

        static int64_t GetPollIntervalTicks(int64_t elapsed_ticks) noexcept
        {
            int64_t e = std::max<int64_t>(0LL, elapsed_ticks);
            if (e < 2LL * TICKS_PER_SECOND)
            {
                return 350LL * TICKS_PER_MILLISECOND;
            }
            if (e < 8LL * TICKS_PER_SECOND)
            {
                return 1LL * TICKS_PER_SECOND;
            }
            return 3LL * TICKS_PER_SECOND;
        }

        static bool CanUseBrowserTitleFallback(bool is_spotify_playing, std::u16string_view media_source) noexcept
        {
            if (is_spotify_playing)
            {
                return false;
            }
            return is_null_or_whitespace(media_source) || parse_platform_view(media_source) == VnMediaPlatform::Browser;
        }

        static bool ShouldContinueAfterJunkTitle(bool is_junk_title, bool is_browser_session) noexcept
        {
            return is_junk_title && is_browser_session;
        }

        static bool ShouldSuppressActiveEmptyPublish(bool is_any_playing, std::u16string_view cur_track) noexcept
        {
            return is_any_playing && is_null_or_whitespace(cur_track);
        }

        static void EvalTransientSessionGap(
            bool has_resolved_session,
            bool has_stable_track,
            std::u16string_view last_source,
            int64_t gap_started_utc_ticks,
            int64_t now_utc_ticks,
            VnTransientSessionGapDecision& out_decision) noexcept
        {
            VnMediaPlatform plat = parse_platform_view(last_source);
            bool recoverable = (plat == VnMediaPlatform::Browser ||
                                plat == VnMediaPlatform::YouTube ||
                                plat == VnMediaPlatform::SoundCloud);
            if (has_resolved_session || !has_stable_track || !recoverable)
            {
                out_decision.should_hold = 0u;
                out_decision.gap_started_utc_ticks = 0;
                return;
            }
            int64_t started = gap_started_utc_ticks;
            if (started == 0 || now_utc_ticks < started)
            {
                started = now_utc_ticks;
            }
            bool should_hold = (now_utc_ticks - started) < (2LL * TICKS_PER_SECOND);
            out_decision.should_hold = should_hold ? 1u : 0u;
            out_decision.gap_started_utc_ticks = started;
        }
    };
} // namespace vnotch::services

using namespace vnotch;
using namespace vnotch::services;

VN_EXPORT int32_t vn_media_recovery_select_detection_mode(
    uint8_t is_any_playing,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    uint8_t is_throttled,
    uint8_t is_session_gap_recovery,
    int32_t* out_mode) noexcept
{
    if (out_mode == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_mode = MediaMetadataRecoveryPolicy::SelectDetectionMode(
        is_any_playing != 0,
        make_u16_view(cur_track_ptr, cur_track_len),
        is_throttled != 0,
        is_session_gap_recovery != 0);
    return VN_OK;
}

VN_EXPORT int32_t vn_media_recovery_get_poll_interval_ticks(int64_t elapsed_ticks, int64_t* out_interval_ticks) noexcept
{
    if (out_interval_ticks == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_interval_ticks = MediaMetadataRecoveryPolicy::GetPollIntervalTicks(elapsed_ticks);
    return VN_OK;
}

VN_EXPORT int32_t vn_media_recovery_can_use_browser_title_fallback(
    uint8_t is_spotify_playing,
    const char16_t* media_source_ptr, int32_t media_source_len,
    uint8_t* out_can_use) noexcept
{
    if (out_can_use == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_can_use = MediaMetadataRecoveryPolicy::CanUseBrowserTitleFallback(
        is_spotify_playing != 0,
        make_u16_view(media_source_ptr, media_source_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_recovery_should_continue_after_junk_title(
    uint8_t is_junk_title,
    uint8_t is_browser_session,
    uint8_t* out_continue) noexcept
{
    if (out_continue == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_continue = MediaMetadataRecoveryPolicy::ShouldContinueAfterJunkTitle(
        is_junk_title != 0, is_browser_session != 0) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_recovery_should_suppress_active_empty_publish(
    uint8_t is_any_playing,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    uint8_t* out_suppress) noexcept
{
    if (out_suppress == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_suppress = MediaMetadataRecoveryPolicy::ShouldSuppressActiveEmptyPublish(
        is_any_playing != 0, make_u16_view(cur_track_ptr, cur_track_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_recovery_eval_transient_session_gap(
    uint8_t has_resolved_session,
    uint8_t has_stable_track,
    const char16_t* last_source_ptr, int32_t last_source_len,
    int64_t gap_started_utc_ticks,
    int64_t now_utc_ticks,
    VnTransientSessionGapDecision* out_decision) noexcept
{
    if (out_decision == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    MediaMetadataRecoveryPolicy::EvalTransientSessionGap(
        has_resolved_session != 0,
        has_stable_track != 0,
        make_u16_view(last_source_ptr, last_source_len),
        gap_started_utc_ticks,
        now_utc_ticks,
        *out_decision);
    return VN_OK;
}
