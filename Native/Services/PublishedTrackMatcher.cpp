#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class PublishedTrackMatcher final
    {
    public:
        static int32_t vn_published_track_is_same_track_impl(const char16_t* last_track_id_ptr, int32_t last_track_id_len,
    const char16_t* last_track_only_ptr, int32_t last_track_only_len,
    const char16_t* last_app_id_ptr, int32_t last_app_id_len,
    const char16_t* last_session_key_ptr, int32_t last_session_key_len,
    const char16_t* exp_track_ptr, int32_t exp_track_len,
    const char16_t* exp_artist_ptr, int32_t exp_artist_len,
    const char16_t* exp_app_id_ptr, int32_t exp_app_id_len,
    const char16_t* exp_session_key_ptr, int32_t exp_session_key_len,
    uint8_t* out_same)
        {
    if (out_same == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view last_track_id = make_u16_view(last_track_id_ptr, last_track_id_len);
    std::u16string_view last_track_only = make_u16_view(last_track_only_ptr, last_track_only_len);
    std::u16string_view last_app_id = make_u16_view(last_app_id_ptr, last_app_id_len);
    std::u16string_view last_session_key = make_u16_view(last_session_key_ptr, last_session_key_len);
    std::u16string_view exp_track = make_u16_view(exp_track_ptr, exp_track_len);
    std::u16string_view exp_artist = make_u16_view(exp_artist_ptr, exp_artist_len);
    std::u16string_view exp_app_id = make_u16_view(exp_app_id_ptr, exp_app_id_len);
    std::u16string_view exp_session_key = make_u16_view(exp_session_key_ptr, exp_session_key_len);

    if (!exp_session_key.empty() && !equals_ordinal(last_session_key, exp_session_key))
    {
        *out_same = 0u;
        return VN_OK;
    }
    if (!exp_app_id.empty() && !equals_ignore_case(last_app_id, exp_app_id))
    {
        *out_same = 0u;
        return VN_OK;
    }

    std::u16string exp_id = build_track_identity_impl(exp_track, exp_artist);
    if (equals_ordinal(last_track_id, exp_id))
    {
        *out_same = 1u;
        return VN_OK;
    }
    if (exp_artist.empty())
    {
        std::u16string exp_track_only = build_track_identity_impl(exp_track, {});
        *out_same = equals_ordinal(last_track_only, exp_track_only) ? 1u : 0u;
        return VN_OK;
    }
    *out_same = 0u;
    return VN_OK;
}

        static int32_t vn_published_track_is_new_for_thumbnail_impl(const char16_t* last_track_only_ptr, int32_t last_track_only_len,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    uint8_t* out_is_new)
        {
    if (out_is_new == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view cur_track = make_u16_view(cur_track_ptr, cur_track_len);
    if (cur_track.empty())
    {
        *out_is_new = 0u;
        return VN_OK;
    }
    std::u16string cur_only = build_track_identity_impl(cur_track, {});
    bool is_new = !equals_ordinal(cur_only, make_u16_view(last_track_only_ptr, last_track_only_len));
    *out_is_new = is_new ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_published_track_is_same_track(const char16_t* last_track_id_ptr, int32_t last_track_id_len,
    const char16_t* last_track_only_ptr, int32_t last_track_only_len,
    const char16_t* last_app_id_ptr, int32_t last_app_id_len,
    const char16_t* last_session_key_ptr, int32_t last_session_key_len,
    const char16_t* exp_track_ptr, int32_t exp_track_len,
    const char16_t* exp_artist_ptr, int32_t exp_artist_len,
    const char16_t* exp_app_id_ptr, int32_t exp_app_id_len,
    const char16_t* exp_session_key_ptr, int32_t exp_session_key_len,
    uint8_t* out_same) noexcept
{
    try { return vnotch::services::PublishedTrackMatcher::vn_published_track_is_same_track_impl(last_track_id_ptr, last_track_id_len, last_track_only_ptr, last_track_only_len, last_app_id_ptr, last_app_id_len, last_session_key_ptr, last_session_key_len, exp_track_ptr, exp_track_len, exp_artist_ptr, exp_artist_len, exp_app_id_ptr, exp_app_id_len, exp_session_key_ptr, exp_session_key_len, out_same);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_published_track_is_new_for_thumbnail(const char16_t* last_track_only_ptr, int32_t last_track_only_len,
    const char16_t* cur_track_ptr, int32_t cur_track_len,
    uint8_t* out_is_new) noexcept
{
    try { return vnotch::services::PublishedTrackMatcher::vn_published_track_is_new_for_thumbnail_impl(last_track_only_ptr, last_track_only_len, cur_track_ptr, cur_track_len, out_is_new);
 } catch (...) { return VN_ERR_FAIL; }
}

