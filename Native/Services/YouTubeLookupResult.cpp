#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class YouTubeLookupResult final
    {
    public:
        static int32_t vn_youtube_title_matches_impl(const char16_t* title_ptr, int32_t title_len,
    const char16_t* other_ptr, int32_t other_len,
    uint8_t* out_matches)
        {
    if (out_matches == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view t1_raw = make_u16_view(title_ptr, title_len);
    std::u16string_view t2_raw = make_u16_view(other_ptr, other_len);
    if (t1_raw.empty() || t2_raw.empty())
    {
        *out_matches = 0u;
        return VN_OK;
    }
    std::u16string t1 = normalize_for_loose_match_impl(t1_raw);
    std::u16string t2 = normalize_for_loose_match_impl(t2_raw);
    if (t1.empty() || t2.empty())
    {
        *out_matches = 0u;
        return VN_OK;
    }
    bool m = contains_ordinal(t1, t2) || contains_ordinal(t2, t1);
    *out_matches = m ? 1u : 0u;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_youtube_title_matches(const char16_t* title_ptr, int32_t title_len,
    const char16_t* other_ptr, int32_t other_len,
    uint8_t* out_matches) noexcept
{
    try { return vnotch::services::YouTubeLookupResult::vn_youtube_title_matches_impl(title_ptr, title_len, other_ptr, other_len, out_matches);
 } catch (...) { return VN_ERR_FAIL; }
}

