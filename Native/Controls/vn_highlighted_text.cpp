#include "../vnotch_native.h"
#include <cwctype>

VN_EXPORT int32_t __stdcall vn_highlighted_text_fill_mask(
    const wchar_t* text_ptr,
    int32_t text_len,
    const wchar_t* query_ptr,
    int32_t query_len,
    uint8_t* out_mask,
    int32_t mask_cap,
    uint8_t* out_matched) noexcept
{
    if (!out_matched || text_len < 0 || query_len < 0)
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_matched = 0;

    if (mask_cap < text_len)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }

    if (text_len > 0)
    {
        if (!text_ptr || !out_mask)
        {
            return VN_ERR_INVALID_ARG;
        }
        std::memset(out_mask, 0, static_cast<size_t>(text_len));
    }

    if (text_len == 0 || query_len == 0)
    {
        return VN_OK;
    }

    if (!query_ptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    uint8_t matched = 0;
    int32_t q_pos = 0;
    while (q_pos < query_len)
    {
        int32_t sep = q_pos;
        while (sep < query_len && query_ptr[sep] != L' ')
        {
            ++sep;
        }

        int32_t tok_start = q_pos;
        int32_t tok_end = sep;
        q_pos = sep + 1;

        while (tok_start < tok_end && std::iswspace(static_cast<wint_t>(query_ptr[tok_start])))
        {
            ++tok_start;
        }
        while (tok_end > tok_start && std::iswspace(static_cast<wint_t>(query_ptr[tok_end - 1])))
        {
            --tok_end;
        }

        const int32_t tok_len = tok_end - tok_start;
        if (tok_len <= 0 || tok_len > text_len)
        {
            continue;
        }

        const wchar_t* const tok_ptr = query_ptr + tok_start;
        const int32_t limit = text_len - tok_len;
        for (int32_t match_at = 0; match_at <= limit; ++match_at)
        {
            if (CompareStringOrdinal(text_ptr + match_at, tok_len, tok_ptr, tok_len, TRUE) == CSTR_EQUAL)
            {
                std::memset(out_mask + match_at, 1, static_cast<size_t>(tok_len));
                matched = 1;
            }
        }
    }

    *out_matched = matched;
    return VN_OK;
}
