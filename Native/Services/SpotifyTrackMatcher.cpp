#include "vnotch_common.h"

namespace vnotch::services
{
    class SpotifyTrackMatcher final
    {
    public:
        static bool IsSpotifyId(std::u16string_view s) noexcept
        {
            if (s.size() != 22) return false;
            for (char16_t ch : s)
            {
                bool ok = (ch >= u'0' && ch <= u'9') || (ch >= u'A' && ch <= u'Z') || (ch >= u'a' && ch <= u'z');
                if (!ok) return false;
            }
            return true;
        }

        static bool ContainsWholePhrase(std::u16string_view text, std::u16string_view phrase) noexcept
        {
            if (phrase.size() < 3 || text.size() < phrase.size()) return false;
            size_t start = 0;
            while (start <= text.size() - phrase.size())
            {
                size_t pos = text.find(phrase, start);
                if (pos == std::u16string_view::npos) return false;
                size_t end = pos + phrase.size();
                if ((pos == 0 || text[pos - 1] == u' ') && (end == text.size() || text[end] == u' '))
                    return true;
                start = pos + 1;
            }
            return false;
        }

        static int32_t MatchNormalizedScore(std::u16string_view cand, std::u16string_view exp, int32_t exact, int32_t contains) noexcept
        {
            if (cand.empty() || exp.empty()) return 0;
            if (equals_ordinal(cand, exp)) return exact;
            if (ContainsWholePhrase(cand, exp) || ContainsWholePhrase(exp, cand)) return contains;
            return 0;
        }

        static bool TryExtractTrackId(std::u16string_view val, std::u16string_view& out_id) noexcept
        {
            if (is_null_or_whitespace(val)) return false;

            auto extract_after = [&](std::u16string_view prefix) noexcept -> bool {
                int32_t idx = index_of_ignore_case(val, prefix);
                if (idx < 0) return false;
                std::u16string_view rem = val.substr(static_cast<size_t>(idx) + prefix.size());
                size_t end = 0;
                while (end < rem.size() && rem[end] != u'?' && rem[end] != u'/' && rem[end] != u'&')
                    ++end;
                std::u16string_view cand = rem.substr(0, end);
                if (IsSpotifyId(cand))
                {
                    out_id = cand;
                    return true;
                }
                return false;
            };

            if (extract_after(u"spotify:track:") || extract_after(u"/track/"))
                return true;

            if (IsSpotifyId(val))
            {
                out_id = val;
                return true;
            }
            return false;
        }

        static int32_t CalculateDurationScoreDelta(int64_t cand_ticks, int64_t exp_ticks) noexcept
        {
            double delta_sec = std::abs(static_cast<double>(cand_ticks - exp_ticks) / static_cast<double>(TICKS_PER_SECOND));
            if (delta_sec <= 4.0) return 12;
            if (delta_sec <= 12.0) return 5;
            if (delta_sec > 45.0) return -15;
            return 0;
        }
    };
}

VN_EXPORT int32_t vn_spotify_normalize_for_match(
    const char16_t* val_ptr, int32_t val_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    if (out_len == nullptr) return VN_ERR_INVALID_ARG;
    try
    {
        std::u16string norm = vnotch::normalize_for_loose_match_impl(vnotch::make_u16_view(val_ptr, val_len));
        return vnotch::copy_u16_out(norm, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_spotify_match_normalized_score(
    const char16_t* cand_ptr, int32_t cand_len,
    const char16_t* exp_ptr, int32_t exp_len,
    int32_t exact, int32_t contains,
    int32_t* out_score) noexcept
{
    if (out_score == nullptr) return VN_ERR_INVALID_ARG;
    *out_score = vnotch::services::SpotifyTrackMatcher::MatchNormalizedScore(
        vnotch::make_u16_view(cand_ptr, cand_len),
        vnotch::make_u16_view(exp_ptr, exp_len),
        exact, contains);
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_extract_track_id(
    const char16_t* val_ptr, int32_t val_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len,
    uint8_t* out_found) noexcept
{
    if (out_len == nullptr || out_found == nullptr) return VN_ERR_INVALID_ARG;
    *out_len = 0;
    *out_found = 0u;

    std::u16string_view id_view{};
    if (vnotch::services::SpotifyTrackMatcher::TryExtractTrackId(vnotch::make_u16_view(val_ptr, val_len), id_view))
    {
        *out_found = 1u;
        return vnotch::copy_u16_out(id_view, out_buf, out_cap, out_len);
    }
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_calculate_duration_score_delta(
    int64_t cand_ticks,
    int64_t exp_ticks,
    int32_t* out_delta) noexcept
{
    if (out_delta == nullptr) return VN_ERR_INVALID_ARG;
    *out_delta = vnotch::services::SpotifyTrackMatcher::CalculateDurationScoreDelta(cand_ticks, exp_ticks);
    return VN_OK;
}
