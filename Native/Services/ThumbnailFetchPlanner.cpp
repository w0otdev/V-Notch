#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ThumbnailFetchPlanner final
    {
    public:
        static int32_t vn_thumbnail_classify_sources_impl(const VnThumbnailSourceInputs* x, VnThumbnailSourcePlan* out_plan) noexcept
        {
    if (x == nullptr || out_plan == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    bool is_potential_yt = (x->platform_is_youtube != 0) ||
                           ((x->platform_is_browser != 0) && (x->is_likely_youtube != 0));
    bool probe_sc = (x->platform_is_browser != 0) &&
                    (x->is_likely_youtube == 0) &&
                    (x->browser_has_youtube_tab_open == 0) &&
                    (x->has_track != 0) &&
                    ((x->thumbnail_is_null_or_placeholder != 0) || (x->has_soundcloud_session_override != 0));
    bool is_potential_sc = (x->platform_is_soundcloud != 0) || probe_sc;

    out_plan->is_potential_youtube = is_potential_yt ? 1u : 0u;
    out_plan->is_potential_soundcloud = is_potential_sc ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_thumbnail_should_start_soundcloud_impl(const VnSoundCloudFetchInputs* x, uint8_t* out_should_start) noexcept
        {
    if (x == nullptr || out_should_start == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (x->is_new_soundcloud_track == 0 && x->same_track_fetch_running != 0)
    {
        *out_should_start = 0u;
        return VN_OK;
    }
    bool retry_placeholder = ((x->thumbnail_is_null_or_placeholder != 0) || (x->has_mismatched_thumb_source != 0)) &&
                             (x->retry_interval_elapsed != 0);
    *out_should_start = ((x->is_new_soundcloud_track != 0) || retry_placeholder) ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_thumbnail_should_start_youtube_impl(const VnYouTubeFetchInputs* x, uint8_t* out_should_start) noexcept
        {
    if (x == nullptr || out_should_start == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (x->is_new_track == 0 && x->same_track_fetch_running != 0)
    {
        *out_should_start = 0u;
        return VN_OK;
    }
    bool start = (x->is_new_track != 0) || ((x->needs_better_thumbnail != 0) && (x->retry_interval_elapsed != 0));
    *out_should_start = start ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_thumbnail_classify_sources(const VnThumbnailSourceInputs* x, VnThumbnailSourcePlan* out_plan) noexcept
{
    return vnotch::services::ThumbnailFetchPlanner::vn_thumbnail_classify_sources_impl(x, out_plan);
}

VN_EXPORT int32_t vn_thumbnail_should_start_soundcloud(const VnSoundCloudFetchInputs* x, uint8_t* out_should_start) noexcept
{
    return vnotch::services::ThumbnailFetchPlanner::vn_thumbnail_should_start_soundcloud_impl(x, out_should_start);
}

VN_EXPORT int32_t vn_thumbnail_should_start_youtube(const VnYouTubeFetchInputs* x, uint8_t* out_should_start) noexcept
{
    return vnotch::services::ThumbnailFetchPlanner::vn_thumbnail_should_start_youtube_impl(x, out_should_start);
}

