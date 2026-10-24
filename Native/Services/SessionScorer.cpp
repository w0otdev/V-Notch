#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class SessionScorer final
    {
    public:
        static int32_t vn_session_scorer_score_impl(const VnSessionScoreInputs* x, int32_t* out_score) noexcept
        {
    if (x == nullptr || out_score == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    int32_t metadata = 0;
    if (x->has_title != 0)
    {
        metadata = 1550;
        if (x->has_artist != 0) metadata += 20;
        if (x->has_thumbnail != 0) metadata += 10;
        if (x->artist_is_non_generic != 0) metadata += 200;
    }

    int32_t source_kind = 0;
    if (x->is_spotify != 0) source_kind = 400;
    else if (x->is_music != 0) source_kind = 400;
    else if (x->is_youtube != 0) source_kind = 350;
    else if (x->is_browser != 0) source_kind = 100;

    int32_t os_and_active = 0;
    if (x->is_os_current != 0)
    {
        os_and_active += (x->dedicated_music_app_playing != 0) ? 200 : 1000;
    }
    if (x->is_active != 0)
    {
        os_and_active += 500;
        if (x->is_os_current != 0 && !(x->is_browser != 0 || x->is_youtube != 0))
        {
            os_and_active += 1000;
        }
    }

    int32_t time_decay = 0;
    if (x->has_play_start_age != 0 && x->play_start_age_seconds >= 0.0 && x->play_start_age_seconds < 45.0)
    {
        time_decay += static_cast<int32_t>(std::max(0.0, 2600.0 - (x->play_start_age_seconds * 45.0)));
    }
    if (x->has_latest_playing_age != 0 && x->latest_playing_age_seconds >= 0.0 && x->latest_playing_age_seconds < 30.0)
    {
        time_decay += static_cast<int32_t>(std::max(0.0, 2200.0 - (x->latest_playing_age_seconds * 60.0)));
    }
    if (x->has_last_playing_idle != 0 && x->last_playing_idle_seconds < 30.0)
    {
        time_decay += static_cast<int32_t>((30.0 - x->last_playing_idle_seconds) * 10.0);
    }
    if (x->has_timeline_age != 0 && x->timeline_age_seconds >= 0.0 && x->timeline_age_seconds < 20.0)
    {
        time_decay += static_cast<int32_t>(std::max(0.0, 200.0 - (x->timeline_age_seconds * 8.0)));
    }

    int32_t timeline_mod = 0;
    if (x->timeline_boost != 0) timeline_mod = 3000;
    else if (x->timeline_penalty != 0) timeline_mod = -3000;

    *out_score = metadata + source_kind + os_and_active + time_decay + timeline_mod;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_session_scorer_score(const VnSessionScoreInputs* x, int32_t* out_score) noexcept
{
    return vnotch::services::SessionScorer::vn_session_scorer_score_impl(x, out_score);
}

