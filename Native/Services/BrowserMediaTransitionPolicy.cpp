#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class BrowserMediaTransitionPolicy final
    {
    public:
        static int32_t vn_browser_media_eval_likely_youtube_ad_impl(uint8_t is_browser_session,
    uint8_t has_stable_track,
    const char16_t* last_source_ptr, int32_t last_source_len,
    const char16_t* last_session_key_ptr, int32_t last_session_key_len,
    const char16_t* cur_session_key_ptr, int32_t cur_session_key_len,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    int64_t cur_duration_ticks,
    uint8_t has_youtube_window,
    uint8_t cur_track_matches_youtube_window,
    int64_t transition_started_utc_ticks,
    int64_t now_utc_ticks,
    VnBrowserAdTransitionDecision* out_decision) noexcept
        {
    if (out_decision == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    bool was_active = transition_started_utc_ticks != 0;
    VnMediaPlatform last_plat = parse_platform_view(make_u16_view(last_source_ptr, last_source_len));
    bool recoverable_source = (last_plat == VnMediaPlatform::Browser || last_plat == VnMediaPlatform::YouTube);

    std::u16string_view last_key = make_u16_view(last_session_key_ptr, last_session_key_len);
    std::u16string_view cur_key = make_u16_view(cur_session_key_ptr, cur_session_key_len);
    bool session_changed = !is_null_or_whitespace(last_key) &&
                           !is_null_or_whitespace(cur_key) &&
                           !equals_ordinal(last_key, cur_key);
    bool ad_sized = cur_duration_ticks <= 0 || cur_duration_ticks <= (2LL * TICKS_PER_MINUTE);
    std::u16string_view cur_track = make_u16_view(cur_track_ptr, cur_track_len);

    bool is_candidate = (is_browser_session != 0) &&
                        (has_stable_track != 0) &&
                        recoverable_source &&
                        session_changed &&
                        !is_null_or_whitespace(cur_track) &&
                        (has_youtube_window != 0) &&
                        (cur_track_matches_youtube_window == 0) &&
                        ad_sized;

    if (!is_candidate)
    {
        out_decision->should_hold = 0u;
        out_decision->transition_started_utc_ticks = 0;
        out_decision->was_transition_active = was_active ? 1u : 0u;
        return VN_OK;
    }

    int64_t started = transition_started_utc_ticks;
    if (started == 0 || now_utc_ticks < started)
    {
        started = now_utc_ticks;
    }
    bool should_hold = (now_utc_ticks - started) < (45LL * TICKS_PER_SECOND);
    out_decision->should_hold = should_hold ? 1u : 0u;
    out_decision->transition_started_utc_ticks = started;
    out_decision->was_transition_active = was_active ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_browser_media_eval_youtube_junk_metadata_impl(uint8_t is_browser_session,
    uint8_t has_stable_track,
    const char16_t* last_source_ptr, int32_t last_source_len,
    uint8_t has_youtube_window,
    int64_t transition_started_utc_ticks,
    int64_t now_utc_ticks,
    VnBrowserAdTransitionDecision* out_decision) noexcept
        {
    if (out_decision == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    bool was_active = transition_started_utc_ticks != 0;
    VnMediaPlatform last_plat = parse_platform_view(make_u16_view(last_source_ptr, last_source_len));
    bool recoverable_source = (last_plat == VnMediaPlatform::Browser || last_plat == VnMediaPlatform::YouTube);
    bool is_candidate = (is_browser_session != 0) &&
                        (has_stable_track != 0) &&
                        recoverable_source &&
                        (has_youtube_window != 0);

    if (!is_candidate)
    {
        out_decision->should_hold = 0u;
        out_decision->transition_started_utc_ticks = 0;
        out_decision->was_transition_active = was_active ? 1u : 0u;
        return VN_OK;
    }

    int64_t started = transition_started_utc_ticks;
    if (started == 0 || now_utc_ticks < started)
    {
        started = now_utc_ticks;
    }
    bool should_hold = (now_utc_ticks - started) < (45LL * TICKS_PER_SECOND);
    out_decision->should_hold = should_hold ? 1u : 0u;
    out_decision->transition_started_utc_ticks = started;
    out_decision->was_transition_active = was_active ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_browser_media_should_carry_youtube_source_impl(uint8_t is_browser_session,
    const char16_t* hint_ptr, int32_t hint_len,
    const char16_t* last_source_ptr, int32_t last_source_len,
    const char16_t* stable_source_ptr, int32_t stable_source_len,
    uint8_t is_completing_ad_transition,
    uint8_t* out_carry) noexcept
        {
    if (out_carry == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view hint = make_u16_view(hint_ptr, hint_len);
    if (is_browser_session == 0 || !equals_ignore_case(hint, u"YouTube"))
    {
        *out_carry = 0u;
        return VN_OK;
    }
    bool carry = (is_completing_ad_transition != 0) ||
                 (parse_platform_view(make_u16_view(last_source_ptr, last_source_len)) == VnMediaPlatform::YouTube) ||
                 (parse_platform_view(make_u16_view(stable_source_ptr, stable_source_len)) == VnMediaPlatform::YouTube);
    *out_carry = carry ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_browser_media_eval_likely_youtube_ad(uint8_t is_browser_session,
    uint8_t has_stable_track,
    const char16_t* last_source_ptr, int32_t last_source_len,
    const char16_t* last_session_key_ptr, int32_t last_session_key_len,
    const char16_t* cur_session_key_ptr, int32_t cur_session_key_len,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    int64_t cur_duration_ticks,
    uint8_t has_youtube_window,
    uint8_t cur_track_matches_youtube_window,
    int64_t transition_started_utc_ticks,
    int64_t now_utc_ticks,
    VnBrowserAdTransitionDecision* out_decision) noexcept
{
    return vnotch::services::BrowserMediaTransitionPolicy::vn_browser_media_eval_likely_youtube_ad_impl(is_browser_session, has_stable_track, last_source_ptr, last_source_len, last_session_key_ptr, last_session_key_len, cur_session_key_ptr, cur_session_key_len, cur_track_ptr, cur_track_len, cur_duration_ticks, has_youtube_window, cur_track_matches_youtube_window, transition_started_utc_ticks, now_utc_ticks, out_decision);
}

VN_EXPORT int32_t vn_browser_media_eval_youtube_junk_metadata(uint8_t is_browser_session,
    uint8_t has_stable_track,
    const char16_t* last_source_ptr, int32_t last_source_len,
    uint8_t has_youtube_window,
    int64_t transition_started_utc_ticks,
    int64_t now_utc_ticks,
    VnBrowserAdTransitionDecision* out_decision) noexcept
{
    return vnotch::services::BrowserMediaTransitionPolicy::vn_browser_media_eval_youtube_junk_metadata_impl(is_browser_session, has_stable_track, last_source_ptr, last_source_len, has_youtube_window, transition_started_utc_ticks, now_utc_ticks, out_decision);
}

VN_EXPORT int32_t vn_browser_media_should_carry_youtube_source(uint8_t is_browser_session,
    const char16_t* hint_ptr, int32_t hint_len,
    const char16_t* last_source_ptr, int32_t last_source_len,
    const char16_t* stable_source_ptr, int32_t stable_source_len,
    uint8_t is_completing_ad_transition,
    uint8_t* out_carry) noexcept
{
    return vnotch::services::BrowserMediaTransitionPolicy::vn_browser_media_should_carry_youtube_source_impl(is_browser_session, hint_ptr, hint_len, last_source_ptr, last_source_len, stable_source_ptr, stable_source_len, is_completing_ad_transition, out_carry);
}

