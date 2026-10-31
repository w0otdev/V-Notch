#include "vnotch_common.h"
#include <array>

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
            source_words.reserve(24);
            query_words.reserve(8);
            SplitSpaces(norm_source, source_words, false);
            SplitSpaces(norm_query, query_words, true);
            if (query_words.empty() || source_words.empty())
                return 0;

            int32_t weakest = 1000;
            int32_t total_score = 0;
            int32_t exact_matches = 0;
            int32_t fuzzy_matches = 0;

            for (std::u16string_view qw : query_words)
            {
                bool is_fuzzy = false;
                int32_t word_score = WordMatchScore(source_words, qw, allow_fuzzy, is_fuzzy);
                if (word_score == 0 || (is_fuzzy && ++fuzzy_matches > 1))
                    return 0;
                if (word_score >= 450)
                    ++exact_matches;
                weakest = std::min(weakest, word_score);
                total_score += word_score;
            }

            int32_t avg_score = total_score / static_cast<int32_t>(query_words.size());
            int32_t phrase_bonus = (query_words.size() > 1 && contains_ordinal(norm_source, norm_query)) ? 95 : 0;
            int32_t exact_bonus = (60 * exact_matches) / static_cast<int32_t>(query_words.size());
            return weakest + (avg_score / 2) + exact_bonus + phrase_bonus;
        }

        static int32_t ScoreRow(
            std::u16string_view norm_title,
            std::u16string_view norm_content,
            std::u16string_view norm_combined,
            std::u16string_view norm_query,
            bool allow_fuzzy)
        {
            if (norm_query.empty()) return 0;

            int32_t title_score = norm_title.empty() ? 0 : GetNormalizedScore(norm_title, norm_query, allow_fuzzy);
            int32_t content_score = norm_content.empty() ? 0 : GetNormalizedScore(norm_content, norm_query, allow_fuzzy);
            int32_t combined_score = norm_combined.empty() ? 0 : GetNormalizedScore(norm_combined, norm_query, false);

            int32_t direct = 0;
            if (title_score > 0 || content_score > 0)
            {
                direct = title_score * 16 + content_score * 10 + 1;
            }
            return std::max(direct, combined_score * 10);
        }

        static bool IsNormalizedMatch(std::u16string_view norm_source, std::u16string_view norm_query)
        {
            return GetNormalizedScore(norm_source, norm_query, true) > 0;
        }

    private:
        struct SynonymGroup
        {
            std::u16string_view terms[8];
            size_t count;
        };

        static constexpr SynonymGroup kSynonymGroups[] = {
            { { u"hotkey", u"shortcut", u"keybind", u"phim", u"tat", u"alt", u"space", u"" }, 7 },
            { { u"audio", u"sound", u"volume", u"speaker", u"mixer", u"equalizer", u"loa", u"" }, 7 },
            { { u"microphone", u"mic", u"camera", u"webcam", u"privacy", u"indicator", u"", u"" }, 6 },
            { { u"theme", u"dark", u"skin", u"appearance", u"glass", u"liquid", u"refraction", u"blur" }, 8 },
            { { u"fps", u"hz", u"framerate", u"frame", u"smooth", u"performance", u"gpu", u"hardware" }, 8 },
            { { u"pin", u"passcode", u"password", u"vault", u"lock", u"personal", u"khoa", u"" }, 7 },
            { { u"clipboard", u"tray", u"shelf", u"file", u"history", u"khay", u"tep", u"" }, 7 },
            { { u"startup", u"boot", u"launch", u"autostart", u"login", u"windows", u"khoi", u"" }, 7 },
            { { u"translate", u"translation", u"language", u"dictionary", u"currency", u"dich", u"ngon", u"" }, 7 },
            { { u"update", u"version", u"changelog", u"release", u"upgrade", u"cap", u"nhat", u"" }, 7 },
            { { u"width", u"height", u"size", u"scale", u"dimension", u"radius", u"corner", u"offset" }, 8 },
            { { u"lyrics", u"subtitle", u"caption", u"karaoke", u"youtube", u"spotify", u"canvas", u"" }, 7 }
        };

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

        static int32_t LevenshteinDistance(std::u16string_view source, std::u16string_view target) noexcept
        {
            if (source.empty()) return static_cast<int32_t>(target.size());
            if (target.empty()) return static_cast<int32_t>(source.size());
            if (target.size() > 62 || source.size() > 62) return 99;

            std::array<int32_t, 64> prev_row{};
            std::array<int32_t, 64> curr_row{};

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
                prev_row = curr_row;
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

        static constexpr bool MatchesAcronymInitials(
            std::span<const std::u16string_view> source_words,
            std::u16string_view query_word) noexcept
        {
            if (query_word.size() < 2 || query_word.size() > 5 || source_words.size() < query_word.size())
                return false;

            for (size_t start = 0; start + query_word.size() <= source_words.size(); ++start)
            {
                bool all_match = true;
                for (size_t k = 0; k < query_word.size(); ++k)
                {
                    std::u16string_view sw = source_words[start + k];
                    if (sw.empty() || sw[0] != query_word[k])
                    {
                        all_match = false;
                        break;
                    }
                }
                if (all_match) return true;
            }
            return false;
        }

        static constexpr bool MatchesSemanticSynonym(
            std::span<const std::u16string_view> source_words,
            std::u16string_view query_word) noexcept
        {
            if (query_word.size() < 3) return false;

            for (const SynonymGroup& group : kSynonymGroups)
            {
                bool query_in_group = false;
                for (size_t i = 0; i < group.count; ++i)
                {
                    std::u16string_view term = group.terms[i];
                    if (term == query_word || (query_word.size() >= 4 && starts_with_ordinal(term, query_word)))
                    {
                        query_in_group = true;
                        break;
                    }
                }
                if (!query_in_group) continue;

                for (std::u16string_view sw : source_words)
                {
                    for (size_t i = 0; i < group.count; ++i)
                    {
                        if (sw == group.terms[i]) return true;
                    }
                }
            }
            return false;
        }

        static int32_t WordMatchScore(
            std::span<const std::u16string_view> source_words,
            std::u16string_view query_word,
            bool allow_fuzzy,
            bool& out_is_fuzzy) noexcept
        {
            out_is_fuzzy = false;
            int32_t best = 0;
            bool unseg = AllUnsegmentedScript(query_word);

            for (size_t idx = 0; idx < source_words.size(); ++idx)
            {
                std::u16string_view sw = source_words[idx];
                if (sw.empty()) continue;

                int32_t pos_bonus = idx == 0 ? 45 : (idx == 1 ? 30 : (idx == 2 ? 15 : 0));

                if (sw == query_word)
                {
                    best = std::max(best, 460 + pos_bonus);
                    continue;
                }

                if (unseg && contains_ordinal(sw, query_word))
                {
                    best = std::max(best, 300 + pos_bonus);
                    continue;
                }

                // Prefix match works for 1+ character queries ("a", "au", "sp", "anim", etc.)
                if (starts_with_ordinal(sw, query_word))
                {
                    int32_t coverage = static_cast<int32_t>((query_word.size() * 70) / std::max<size_t>(1, sw.size()));
                    best = std::max(best, 320 + coverage + pos_bonus);
                    continue;
                }

                // Mid-word / compound substring match for 3+ character queries ("screen" -> "fullscreen", "light" -> "spotlight")
                if (query_word.size() >= 3 && contains_ordinal(sw, query_word))
                {
                    int32_t coverage = static_cast<int32_t>((query_word.size() * 45) / std::max<size_t>(1, sw.size()));
                    best = std::max(best, 230 + coverage + (pos_bonus / 2));
                    continue;
                }
            }

            if (best > 0) return best;

            if (MatchesAcronymInitials(source_words, query_word))
                return 210;

            if (MatchesSemanticSynonym(source_words, query_word))
                return 185;

            if (!allow_fuzzy || query_word.size() < 4)
                return 0;

            for (std::u16string_view sw : source_words)
            {
                if (sw.size() < 4 || query_word[0] != sw[0])
                    continue;

                int32_t len_diff = std::abs(static_cast<int32_t>(query_word.size()) - static_cast<int32_t>(sw.size()));
                if (len_diff <= 1 &&
                    (LevenshteinDistance(query_word, sw) == 1 || IsSingleTransposition(query_word, sw)))
                {
                    out_is_fuzzy = true;
                    return 120;
                }

                if (query_word.size() >= 5 && sw.size() > query_word.size())
                {
                    std::u16string_view prefix = sw.substr(0, query_word.size());
                    if (LevenshteinDistance(query_word, prefix) == 1 || IsSingleTransposition(query_word, prefix))
                    {
                        out_is_fuzzy = true;
                        return 110;
                    }
                }
            }

            return 0;
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
    try {
        std::u16string res = vnotch::services::SettingsSearchMatcher::Normalize(vnotch::make_u16_view(input_ptr, input_len));
        return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
    } catch (...) { return VN_ERR_FAIL; }
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
    try {
        *out_score = vnotch::services::SettingsSearchMatcher::GetNormalizedScore(
            vnotch::make_u16_view(norm_source_ptr, norm_source_len),
            vnotch::make_u16_view(norm_query_ptr, norm_query_len),
            allow_fuzzy != 0);
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_settings_search_score_row(
    const char16_t* norm_title_ptr,
    int32_t norm_title_len,
    const char16_t* norm_content_ptr,
    int32_t norm_content_len,
    const char16_t* norm_combined_ptr,
    int32_t norm_combined_len,
    const char16_t* norm_query_ptr,
    int32_t norm_query_len,
    uint8_t allow_fuzzy,
    int32_t* out_score) noexcept
{
    if (out_score == nullptr) return VN_ERR_INVALID_ARG;
    try {
        *out_score = vnotch::services::SettingsSearchMatcher::ScoreRow(
            vnotch::make_u16_view(norm_title_ptr, norm_title_len),
            vnotch::make_u16_view(norm_content_ptr, norm_content_len),
            vnotch::make_u16_view(norm_combined_ptr, norm_combined_len),
            vnotch::make_u16_view(norm_query_ptr, norm_query_len),
            allow_fuzzy != 0);
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_settings_search_is_match(
    const char16_t* source_ptr,
    int32_t source_len,
    const char16_t* query_ptr,
    int32_t query_len,
    uint8_t* out_is_match) noexcept
{
    if (out_is_match == nullptr) return VN_ERR_INVALID_ARG;
    try {
        std::u16string ns = vnotch::services::SettingsSearchMatcher::Normalize(vnotch::make_u16_view(source_ptr, source_len));
        std::u16string nq = vnotch::services::SettingsSearchMatcher::Normalize(vnotch::make_u16_view(query_ptr, query_len));
        *out_is_match = vnotch::services::SettingsSearchMatcher::IsNormalizedMatch(ns, nq) ? 1u : 0u;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
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
