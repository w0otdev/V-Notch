#include "vnotch_common.h"
#include <algorithm>

VN_EXPORT int32_t vn_settings_search_get_normalized_score(
    const char16_t* candidate_ptr, int32_t candidate_len,
    const char16_t* query_ptr, int32_t query_len,
    uint8_t allow_fuzzy,
    int32_t* out_score) noexcept;

namespace vnotch::services
{

class SystemCommandProvider final
{
    static bool ContainsWordOrdinal(std::u16string_view term, std::u16string_view word) noexcept
    {
        size_t i = 0;
        while (i < term.size())
        {
            size_t next = term.find(u' ', i);
            std::u16string_view token = (next == std::u16string_view::npos)
                ? term.substr(i)
                : term.substr(i, next - i);
            if (token == word)
            {
                return true;
            }
            if (next == std::u16string_view::npos)
            {
                break;
            }
            i = next + 1;
        }
        return false;
    }

public:
    static double ScoreNormalizedTerm(std::u16string_view term, std::u16string_view query) noexcept
    {
        if (term.empty() || query.empty())
        {
            return 0.0;
        }
        if (term == query)
        {
            return 1450.0;
        }
        if (vnotch::starts_with_ordinal(term, query))
        {
            int32_t diff = static_cast<int32_t>(term.size() - query.size());
            return 1250.0 - static_cast<double>(std::min(100, diff));
        }
        if (ContainsWordOrdinal(term, query))
        {
            return 1100.0;
        }
        int32_t match_score = 0;
        vn_settings_search_get_normalized_score(
            term.data(), static_cast<int32_t>(term.size()),
            query.data(), static_cast<int32_t>(query.size()),
            0u, &match_score);
        if (match_score > 0)
        {
            return 850.0;
        }
        return 0.0;
    }

    static double ScoreEntry(
        std::u16string_view keywords,
        std::u16string_view title,
        std::u16string_view file_name,
        std::u16string_view normalized_query)
    {
        if (normalized_query.empty())
        {
            return 0.0;
        }
        double best = 0.0;
        size_t i = 0;
        while (i <= keywords.size())
        {
            size_t sep = keywords.find(u';', i);
            std::u16string_view raw_term = (sep == std::u16string_view::npos)
                ? keywords.substr(i)
                : keywords.substr(i, sep - i);
            std::u16string norm_term = vnotch::settings_search_normalize_impl(raw_term);
            best = std::max(best, ScoreNormalizedTerm(norm_term, normalized_query));
            if (sep == std::u16string_view::npos)
            {
                break;
            }
            i = sep + 1;
        }

        if (!title.empty())
        {
            std::u16string norm_title = vnotch::settings_search_normalize_impl(title);
            best = std::max(best, ScoreNormalizedTerm(norm_title, normalized_query));
        }
        if (!file_name.empty())
        {
            std::u16string norm_file = vnotch::settings_search_normalize_impl(file_name);
            best = std::max(best, ScoreNormalizedTerm(norm_file, normalized_query));
        }
        return best;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_system_command_provider_score_entry(
    const char16_t* keywords_ptr, int32_t keywords_len,
    const char16_t* title_ptr, int32_t title_len,
    const char16_t* file_name_ptr, int32_t file_name_len,
    const char16_t* norm_query_ptr, int32_t norm_query_len,
    double* out_score) noexcept
{
    if (out_score == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    try
    {
        *out_score = vnotch::services::SystemCommandProvider::ScoreEntry(
            vnotch::make_u16_view(keywords_ptr, keywords_len),
            vnotch::make_u16_view(title_ptr, title_len),
            vnotch::make_u16_view(file_name_ptr, file_name_len),
            vnotch::make_u16_view(norm_query_ptr, norm_query_len));
        return VN_OK;
    }
    catch (...)
    {
        *out_score = 0.0;
        return VN_ERR_FAIL;
    }
}

} // extern "C"
