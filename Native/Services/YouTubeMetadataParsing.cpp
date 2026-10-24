#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class YouTubeMetadataParsing final
    {
    public:
        static int32_t vn_youtube_looks_like_quota_exceeded_impl(const char16_t* body_ptr, int32_t body_len, uint8_t* out_exceeded) noexcept
        {
    if (out_exceeded == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    std::u16string_view sv = make_u16_view(body_ptr, body_len);
    if (is_null_or_whitespace(sv))
    {
        *out_exceeded = 0u;
        return VN_OK;
    }
    bool ex = contains_ordinal(sv, u"quotaExceeded") ||
              contains_ordinal(sv, u"dailyLimitExceeded") ||
              contains_ordinal(sv, u"rateLimitExceeded");
    *out_exceeded = ex ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_youtube_parse_iso8601_duration_ticks_impl(const char16_t* iso_ptr, int32_t iso_len, int64_t* out_ticks) noexcept
        {
    if (out_ticks == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_ticks = 0;
    std::u16string_view sv = make_u16_view(iso_ptr, iso_len);
    int32_t pt_pos = index_of_ordinal(sv, u"PT");
    if (pt_pos < 0)
    {
        return VN_OK;
    }
    size_t idx = static_cast<size_t>(pt_pos + 2);
    int64_t hours = 0;
    int64_t minutes = 0;
    int64_t seconds = 0;
    char last_unit = 0;

    while (idx < sv.size())
    {
        size_t num_start = idx;
        int64_t val = 0;
        bool overflow = false;
        while (idx < sv.size() && sv[idx] >= u'0' && sv[idx] <= u'9')
        {
            int64_t digit = static_cast<int64_t>(sv[idx] - u'0');
            if (val > (0x7FFFFFFFLL - digit) / 10LL)
            {
                overflow = true;
            }
            val = val * 10LL + digit;
            ++idx;
        }
        if (idx == num_start || idx >= sv.size())
        {
            break;
        }
        char16_t unit = sv[idx];
        if (unit == u'H' && last_unit == 0)
        {
            if (overflow) { *out_ticks = 0; return VN_OK; }
            hours = val;
            last_unit = 'H';
            ++idx;
        }
        else if (unit == u'M' && (last_unit == 0 || last_unit == 'H'))
        {
            if (overflow) { *out_ticks = 0; return VN_OK; }
            minutes = val;
            last_unit = 'M';
            ++idx;
        }
        else if (unit == u'S' && (last_unit == 0 || last_unit == 'H' || last_unit == 'M'))
        {
            if (overflow) { *out_ticks = 0; return VN_OK; }
            seconds = val;
            last_unit = 'S';
            ++idx;
            break;
        }
        else
        {
            break;
        }
    }
    *out_ticks = hours * TICKS_PER_HOUR + minutes * TICKS_PER_MINUTE + seconds * TICKS_PER_SECOND;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_youtube_looks_like_quota_exceeded(const char16_t* body_ptr, int32_t body_len, uint8_t* out_exceeded) noexcept
{
    return vnotch::services::YouTubeMetadataParsing::vn_youtube_looks_like_quota_exceeded_impl(body_ptr, body_len, out_exceeded);
}

VN_EXPORT int32_t vn_youtube_parse_iso8601_duration_ticks(const char16_t* iso_ptr, int32_t iso_len, int64_t* out_ticks) noexcept
{
    return vnotch::services::YouTubeMetadataParsing::vn_youtube_parse_iso8601_duration_ticks_impl(iso_ptr, iso_len, out_ticks);
}

