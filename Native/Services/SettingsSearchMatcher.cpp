#include "vnotch_common.h"

namespace vnotch::services
{
    class SettingsSearchMatcher final
    {
    public:
        static std::u16string Normalize(std::u16string_view input)
        {
            return settings_search_normalize_impl(input);
        }

        static int32_t GetNormalizedScore(std::u16string_view norm_source, std::u16string_view norm_query, bool allow_fuzzy)
        {
            if (norm_source.empty() || norm_query.empty())
                return 0;

            std::vector<std::u16string_view> source_words;
            std::vector<std::u16string_view> query_words;
            source_words.reserve(16);
            query_words.reserve(8);
            SplitSpaces(norm_source, source_words, false);
            SplitSpaces(norm_query, query_words, true);
            if (query_words.empty() || source_words.empty())
                return 0;

            int32_t weakest = 3;
            int32_t exact_matches = 0;
            int32_t fuzzy_matches = 0;
            for (std::u16string_view qw : query_words)
            {
                int32_t quality = WordMatchQuality(source_words, qw, allow_fuzzy);
                if (quality == 0 || (quality == 1 && ++fuzzy_matches > 1))
                    return 0;
                if (quality == 3)
                    ++exact_matches;
                weakest = std::min(weakest, quality);
            }
            return weakest * 100 + (50 * exact_matches) / static_cast<int32_t>(query_words.size());
        }

        static bool IsNormalizedMatch(std::u16string_view norm_source, std::u16string_view norm_query)
        {
            return GetNormalizedScore(norm_source, norm_query, true) > 0;
        }

    private:
        static constexpr bool IsUnsegmentedScriptChar(char16_t ch) noexcept
        {
            return (ch >= 0x3040u && ch <= 0x30FFu) ||
                   (ch >= 0x3400u && ch <= 0x9FFFu) ||
                   (ch >= 0x1100u && ch <= 0x11FFu) ||
                   (ch >= 0x0E00u && ch <= 0x0EFFu) ||
                   (ch >= 0x1000u && ch <= 0x109Fu) ||
                   (ch >= 0x1780u && ch <= 0x17FFu);
        }

        static constexpr bool AllUnsegmentedScript(std::u16string_view word) noexcept
        {
            if (word.empty()) return false;
            for (char16_t ch : word)
            {
                if (!IsUnsegmentedScriptChar(ch)) return false;
            }
            return true;
        }

        static constexpr bool IsSingleTransposition(std::u16string_view a, std::u16string_view b) noexcept
        {
            if (a.size() != b.size()) return false;
            int32_t first_diff = -1;
            int32_t second_diff = -1;
            for (int32_t i = 0; i < static_cast<int32_t>(a.size()); ++i)
            {
                if (a[static_cast<size_t>(i)] != b[static_cast<size_t>(i)])
                {
                    if (first_diff == -1) first_diff = i;
                    else if (second_diff == -1) second_diff = i;
                    else return false;
                }
            }
            return first_diff != -1 &&
                   second_diff == first_diff + 1 &&
                   a[static_cast<size_t>(first_diff)] == b[static_cast<size_t>(second_diff)] &&
                   a[static_cast<size_t>(second_diff)] == b[static_cast<size_t>(first_diff)];
        }

        static int32_t LevenshteinDistance(std::u16string_view source, std::u16string_view target)
        {
            if (source.empty()) return static_cast<int32_t>(target.size());
            if (target.empty()) return static_cast<int32_t>(source.size());

            std::vector<int32_t> prev_row(target.size() + 1);
            std::vector<int32_t> curr_row(target.size() + 1);

            for (size_t col = 0; col <= target.size(); ++col)
                prev_row[col] = static_cast<int32_t>(col);

            for (size_t row = 0; row < source.size(); ++row)
            {
                curr_row[0] = static_cast<int32_t>(row + 1);
                for (size_t col = 0; col < target.size(); ++col)
                {
                    int32_t cost = (source[row] == target[col]) ? 0 : 1;
                    curr_row[col + 1] = std::min({
                        curr_row[col] + 1,
                        prev_row[col + 1] + 1,
                        prev_row[col] + cost
                    });
                }
                std::swap(prev_row, curr_row);
            }
            return prev_row[target.size()];
        }

        static void SplitSpaces(std::u16string_view text, std::vector<std::u16string_view>& out_words, bool deduplicate)
        {
            out_words.clear();
            size_t i = 0;
            while (i < text.size())
            {
                while (i < text.size() && text[i] == u' ') ++i;
                if (i >= text.size()) break;
                size_t start = i;
                while (i < text.size() && text[i] != u' ') ++i;
                std::u16string_view word = text.substr(start, i - start);
                if (deduplicate)
                {
                    bool exists = false;
                    for (std::u16string_view existing : out_words)
                    {
                        if (existing == word) { exists = true; break; }
                    }
                    if (!exists) out_words.push_back(word);
                }
                else
                {
                    out_words.push_back(word);
                }
            }
        }

        static int32_t WordMatchQuality(
            std::span<const std::u16string_view> source_words,
            std::u16string_view query_word,
            bool allow_fuzzy)
        {
            int32_t best = 0;
            bool unseg = AllUnsegmentedScript(query_word);
            for (std::u16string_view sw : source_words)
            {
                if (sw == query_word) return 3;
                if (unseg && contains_ordinal(sw, query_word))
                {
                    best = std::max(best, 2);
                    continue;
                }
                if (query_word.size() >= 3 && starts_with_ordinal(sw, query_word))
                {
                    best = std::max(best, 2);
                    continue;
                }
                if (allow_fuzzy && best == 0 &&
                    query_word.size() >= 5 && sw.size() >= 5 &&
                    query_word[0] == sw[0] &&
                    std::abs(static_cast<int32_t>(query_word.size()) - static_cast<int32_t>(sw.size())) <= 1 &&
                    (LevenshteinDistance(query_word, sw) == 1 || IsSingleTransposition(query_word, sw)))
                {
                    best = 1;
                }
            }
            return best;
        }
    };
}

VN_EXPORT int32_t vn_settings_search_normalize(
    const char16_t* input_ptr,
    int32_t input_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    std::u16string res = vnotch::services::SettingsSearchMatcher::Normalize(vnotch::make_u16_view(input_ptr, input_len));
    return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_settings_search_get_normalized_score(
    const char16_t* norm_source_ptr,
    int32_t norm_source_len,
    const char16_t* norm_query_ptr,
    int32_t norm_query_len,
    uint8_t allow_fuzzy,
    int32_t* out_score) noexcept
{
    if (out_score == nullptr) return VN_ERR_INVALID_ARG;
    *out_score = vnotch::services::SettingsSearchMatcher::GetNormalizedScore(
        vnotch::make_u16_view(norm_source_ptr, norm_source_len),
        vnotch::make_u16_view(norm_query_ptr, norm_query_len),
        allow_fuzzy != 0);
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_search_is_match(
    const char16_t* source_ptr,
    int32_t source_len,
    const char16_t* query_ptr,
    int32_t query_len,
    uint8_t* out_is_match) noexcept
{
    if (out_is_match == nullptr) return VN_ERR_INVALID_ARG;
    std::u16string ns = vnotch::services::SettingsSearchMatcher::Normalize(vnotch::make_u16_view(source_ptr, source_len));
    std::u16string nq = vnotch::services::SettingsSearchMatcher::Normalize(vnotch::make_u16_view(query_ptr, query_len));
    *out_is_match = vnotch::services::SettingsSearchMatcher::IsNormalizedMatch(ns, nq) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_settings_search_is_normalized_match(
    const char16_t* candidate_ptr,
    int32_t candidate_len,
    const char16_t* query_ptr,
    int32_t query_len,
    uint8_t* out_result) noexcept
{
    if (out_result == nullptr) return VN_ERR_INVALID_ARG;
    *out_result = vnotch::services::SettingsSearchMatcher::IsNormalizedMatch(
        vnotch::make_u16_view(candidate_ptr, candidate_len),
        vnotch::make_u16_view(query_ptr, query_len)) ? 1u : 0u;
    return VN_OK;
}
