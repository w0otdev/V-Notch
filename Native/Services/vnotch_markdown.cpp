#include "../vnotch_native.h"

#ifndef VN_MARKDOWN_BOUNDARY_SUMMARY_DEFINED
#define VN_MARKDOWN_BOUNDARY_SUMMARY_DEFINED
#pragma pack(push, 1)
struct VnMarkdownBoundarySummary
{
    uint8_t has_unclosed_fence;
    uint8_t fence_char;
    uint8_t fence_count;
    uint8_t has_unclosed_inline_code;
    uint8_t inline_code_ticks;
    uint8_t has_unclosed_bold;
    uint8_t bold_char;
    uint8_t has_unclosed_italic;
    uint8_t italic_char;
    uint8_t bold_outer;
};
#pragma pack(pop)
#endif

namespace
{
    constexpr bool is_unicode_ws(char16_t ch) noexcept
    {
        if (ch <= 0x0020u)
        {
            return ch == u' ' || ch == u'\t' || ch == u'\n' || ch == u'\v' || ch == u'\f' || ch == u'\r';
        }
        if (ch < 0x0085u)
        {
            return false;
        }
        return ch == 0x0085u || ch == 0x00A0u || ch == 0x1680u ||
               (ch >= 0x2000u && ch <= 0x200Au) ||
               ch == 0x2028u || ch == 0x2029u || ch == 0x202Fu || ch == 0x205Fu || ch == 0x3000u;
    }

    constexpr bool is_digit(char16_t ch) noexcept
    {
        return ch >= u'0' && ch <= u'9';
    }

    constexpr bool is_alpha(char16_t ch) noexcept
    {
        return (ch >= u'a' && ch <= u'z') || (ch >= u'A' && ch <= u'Z');
    }

    constexpr bool is_word_char_unicode(char16_t ch) noexcept
    {
        if (ch <= 0x007Fu)
        {
            return is_alpha(ch) || is_digit(ch);
        }
        if (is_unicode_ws(ch))
        {
            return false;
        }
        if ((ch >= 0x2000u && ch <= 0x206Fu) ||
            (ch >= 0x3000u && ch <= 0x303Fu) ||
            (ch >= 0xFF00u && ch <= 0xFF0Fu))
        {
            return false;
        }
        return true;
    }

    constexpr char16_t to_lower_ascii(char16_t ch) noexcept
    {
        return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
    }

    constexpr bool is_url_terminator(char16_t ch) noexcept
    {
        return is_unicode_ws(ch) || ch == u'<' || ch == u'>' || ch == u'"' ||
               ch == u'\'' || ch == u'`' || ch == u')' || ch == u']';
    }

    constexpr bool starts_with_http_scheme(const char16_t* text, int32_t limit, int32_t pos, int32_t* out_scheme_len) noexcept
    {
        if (pos + 7 <= limit &&
            to_lower_ascii(text[pos]) == u'h' &&
            to_lower_ascii(text[pos + 1]) == u't' &&
            to_lower_ascii(text[pos + 2]) == u't' &&
            to_lower_ascii(text[pos + 3]) == u'p')
        {
            if (pos + 8 <= limit &&
                to_lower_ascii(text[pos + 4]) == u's' &&
                text[pos + 5] == u':' &&
                text[pos + 6] == u'/' &&
                text[pos + 7] == u'/')
            {
                *out_scheme_len = 8;
                return true;
            }
            if (text[pos + 4] == u':' &&
                text[pos + 5] == u'/' &&
                text[pos + 6] == u'/')
            {
                *out_scheme_len = 7;
                return true;
            }
        }
        return false;
    }
}

extern "C" VN_EXPORT int32_t __stdcall vn_markdown_scan_word_spans(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownSlice* out_spans,
    int32_t span_cap,
    int32_t* out_written) noexcept
{
    if (text_len < 0 || (text_len > 0 && text_ptr == nullptr) ||
        span_cap < 0 || (span_cap > 0 && out_spans == nullptr) ||
        out_written == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_written = 0;
    if (text_len == 0)
    {
        return VN_OK;
    }

    int32_t count = 0;
    int32_t i = 0;
    while (i < text_len && is_unicode_ws(text_ptr[i]))
    {
        ++i;
    }

    while (i < text_len)
    {
        int32_t start = i;
        while (i < text_len && !is_unicode_ws(text_ptr[i]))
        {
            ++i;
        }
        while (i < text_len && is_unicode_ws(text_ptr[i]))
        {
            ++i;
        }

        if (out_spans != nullptr && count < span_cap)
        {
            out_spans[count] = { start, i - start };
        }
        ++count;
    }

    *out_written = count;
    if (out_spans != nullptr && count > span_cap)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_markdown_scan_span_boundaries(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownBoundarySummary* out_summary) noexcept
{
    if (text_len < 0 || (text_len > 0 && text_ptr == nullptr) || out_summary == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_summary = {};
    if (text_len == 0)
    {
        return VN_OK;
    }

    bool in_fence = false;
    char16_t fence_char = 0;
    int32_t fence_len = 0;

    int32_t open_code_ticks = 0;
    char16_t open_bold_char = 0;
    char16_t open_italic_char = 0;
    uint8_t bold_outer = 0;

    int32_t pos = 0;
    while (pos < text_len)
    {
        int32_t line_start = pos;
        while (pos < text_len && text_ptr[pos] != u'\n')
        {
            ++pos;
        }
        int32_t content_end = pos;
        if (content_end > line_start && text_ptr[content_end - 1] == u'\r')
        {
            --content_end;
        }
        if (pos < text_len && text_ptr[pos] == u'\n')
        {
            ++pos;
        }

        int32_t indent_spaces = 0;
        int32_t p = line_start;
        while (p < content_end && text_ptr[p] == u' ')
        {
            ++indent_spaces;
            ++p;
        }

        int32_t ws_end = p;
        while (ws_end < content_end && is_unicode_ws(text_ptr[ws_end]))
        {
            ++ws_end;
        }
        const bool is_blank_line = (ws_end == content_end);

        if (in_fence)
        {
            if (indent_spaces <= 3 && p < content_end && text_ptr[p] == fence_char)
            {
                int32_t close_len = 0;
                while (p + close_len < content_end && text_ptr[p + close_len] == fence_char)
                {
                    ++close_len;
                }
                int32_t tail = p + close_len;
                while (tail < content_end && (text_ptr[tail] == u' ' || text_ptr[tail] == u'\t'))
                {
                    ++tail;
                }
                if (close_len >= fence_len && tail == content_end)
                {
                    in_fence = false;
                    fence_char = 0;
                    fence_len = 0;
                }
            }
            continue;
        }

        if (is_blank_line)
        {
            open_code_ticks = 0;
            open_bold_char = 0;
            open_italic_char = 0;
            bold_outer = 0;
            continue;
        }

        if (open_code_ticks == 0 &&
            indent_spaces <= 3 &&
            p + 3 <= content_end &&
            (text_ptr[p] == u'`' || text_ptr[p] == u'~'))
        {
            const char16_t f_ch = text_ptr[p];
            int32_t f_len = 0;
            while (p + f_len < content_end && text_ptr[p + f_len] == f_ch)
            {
                ++f_len;
            }

            bool valid_fence = (f_len >= 3);
            if (valid_fence && f_ch == u'`')
            {
                for (int32_t k = p + f_len; k < content_end; ++k)
                {
                    if (text_ptr[k] == u'`')
                    {
                        valid_fence = false;
                        break;
                    }
                }
            }

            if (valid_fence)
            {
                in_fence = true;
                fence_char = f_ch;
                fence_len = f_len > 255 ? 255 : f_len;
                open_bold_char = 0;
                open_italic_char = 0;
                bold_outer = 0;
                continue;
            }
        }

        int32_t scan_start = line_start;
        if (open_code_ticks == 0)
        {
            int32_t cur = ws_end;
            while (cur < content_end && text_ptr[cur] == u'>')
            {
                ++cur;
                while (cur < content_end && (text_ptr[cur] == u' ' || text_ptr[cur] == u'\t'))
                {
                    ++cur;
                }
            }

            if (cur < content_end)
            {
                const char16_t lead = text_ptr[cur];

                if ((lead == u'-' || lead == u'*' || lead == u'_') && content_end - cur >= 3)
                {
                    bool all_rule = true;
                    int32_t rule_count = 0;
                    for (int32_t k = cur; k < content_end; ++k)
                    {
                        if (text_ptr[k] == lead)
                        {
                            ++rule_count;
                        }
                        else if (text_ptr[k] != u' ' && text_ptr[k] != u'\t')
                        {
                            all_rule = false;
                            break;
                        }
                    }
                    if (all_rule && rule_count >= 3)
                    {
                        open_bold_char = 0;
                        open_italic_char = 0;
                        bold_outer = 0;
                        continue;
                    }
                }

                if (lead == u'#')
                {
                    int32_t hashes = 0;
                    while (cur + hashes < content_end && hashes < 6 && text_ptr[cur + hashes] == u'#')
                    {
                        ++hashes;
                    }
                    if (cur + hashes == content_end ||
                        text_ptr[cur + hashes] == u' ' ||
                        text_ptr[cur + hashes] == u'\t')
                    {
                        open_bold_char = 0;
                        open_italic_char = 0;
                        bold_outer = 0;
                        cur += hashes;
                        while (cur < content_end && (text_ptr[cur] == u' ' || text_ptr[cur] == u'\t'))
                        {
                            ++cur;
                        }
                    }
                }
                else if ((lead == u'*' || lead == u'-' || lead == u'+') &&
                         cur + 1 < content_end &&
                         (text_ptr[cur + 1] == u' ' || text_ptr[cur + 1] == u'\t'))
                {
                    open_bold_char = 0;
                    open_italic_char = 0;
                    bold_outer = 0;
                    cur += 2;
                    while (cur < content_end && (text_ptr[cur] == u' ' || text_ptr[cur] == u'\t'))
                    {
                        ++cur;
                    }
                }
                else if (is_digit(lead))
                {
                    int32_t d = cur;
                    while (d < content_end && is_digit(text_ptr[d]))
                    {
                        ++d;
                    }
                    if (d + 1 < content_end &&
                        (text_ptr[d] == u'.' || text_ptr[d] == u')') &&
                        (text_ptr[d + 1] == u' ' || text_ptr[d + 1] == u'\t'))
                    {
                        open_bold_char = 0;
                        open_italic_char = 0;
                        bold_outer = 0;
                        cur = d + 2;
                        while (cur < content_end && (text_ptr[cur] == u' ' || text_ptr[cur] == u'\t'))
                        {
                            ++cur;
                        }
                    }
                }
            }

            scan_start = cur;
        }

        int32_t i = scan_start;
        while (i < content_end)
        {
            const char16_t ch = text_ptr[i];

            if (open_code_ticks > 0)
            {
                if (ch == u'`')
                {
                    int32_t run = 1;
                    while (i + run < content_end && text_ptr[i + run] == u'`')
                    {
                        ++run;
                    }
                    if (run == open_code_ticks)
                    {
                        open_code_ticks = 0;
                    }
                    i += run;
                    continue;
                }
                ++i;
                continue;
            }

            if (ch == u'\\')
            {
                i += (i + 1 < content_end) ? 2 : 1;
                continue;
            }

            if (ch == u'`')
            {
                int32_t run = 1;
                while (i + run < content_end && text_ptr[i + run] == u'`')
                {
                    ++run;
                }
                open_code_ticks = run > 255 ? 255 : run;
                i += run;
                continue;
            }

            if (ch == u']' && i + 1 < content_end && text_ptr[i + 1] == u'(')
            {
                int32_t dest = i + 2;
                while (dest < content_end && !is_unicode_ws(text_ptr[dest]) && text_ptr[dest] != u')')
                {
                    ++dest;
                }
                if (dest < content_end && text_ptr[dest] == u')')
                {
                    ++dest;
                }
                i = dest;
                continue;
            }

            if ((ch == u'h' || ch == u'H') && (i == scan_start || !is_word_char_unicode(text_ptr[i - 1])))
            {
                int32_t scheme_len = 0;
                if (starts_with_http_scheme(text_ptr, content_end, i, &scheme_len))
                {
                    int32_t url_end = i + scheme_len;
                    while (url_end < content_end && !is_url_terminator(text_ptr[url_end]))
                    {
                        ++url_end;
                    }
                    i = url_end;
                    continue;
                }
            }

            if (ch == u'*' || ch == u'_')
            {
                int32_t run = 1;
                while (i + run < content_end && text_ptr[i + run] == ch)
                {
                    ++run;
                }

                const char16_t prev_ch = (i > scan_start) ? text_ptr[i - 1] : u' ';
                const char16_t next_ch = (i + run < content_end) ? text_ptr[i + run] : u' ';

                const bool left_flanking = !is_unicode_ws(next_ch);
                const bool right_flanking = !is_unicode_ws(prev_ch);

                const bool can_open = left_flanking &&
                                      (ch == u'*' || !is_word_char_unicode(prev_ch));
                const bool can_close = right_flanking &&
                                       (ch == u'*' || !is_word_char_unicode(next_ch));

                int32_t rem = run;
                if (can_close)
                {
                    if (rem >= 2 && open_bold_char == ch)
                    {
                        open_bold_char = 0;
                        rem -= 2;
                        if (open_italic_char != 0)
                        {
                            bold_outer = 0;
                        }
                    }
                    if (rem >= 1 && open_italic_char == ch)
                    {
                        open_italic_char = 0;
                        rem -= 1;
                    }
                }

                if (can_open && rem > 0 && rem <= 3)
                {
                    if (rem >= 2 && open_bold_char == 0)
                    {
                        open_bold_char = ch;
                        if (open_italic_char == 0)
                        {
                            bold_outer = 1;
                        }
                        rem -= 2;
                    }
                    if (rem >= 1 && open_italic_char == 0)
                    {
                        open_italic_char = ch;
                        if (open_bold_char == 0)
                        {
                            bold_outer = 0;
                        }
                        rem -= 1;
                    }
                }

                i += run;
                continue;
            }

            ++i;
        }
    }

    if (in_fence)
    {
        out_summary->has_unclosed_fence = 1u;
        out_summary->fence_char = static_cast<uint8_t>(fence_char);
        out_summary->fence_count = static_cast<uint8_t>(fence_len);
        return VN_OK;
    }

    if (open_code_ticks > 0)
    {
        out_summary->has_unclosed_inline_code = 1u;
        out_summary->inline_code_ticks = static_cast<uint8_t>(open_code_ticks);
        return VN_OK;
    }

    if (open_bold_char != 0)
    {
        out_summary->has_unclosed_bold = 1u;
        out_summary->bold_char = static_cast<uint8_t>(open_bold_char);
    }
    if (open_italic_char != 0)
    {
        out_summary->has_unclosed_italic = 1u;
        out_summary->italic_char = static_cast<uint8_t>(open_italic_char);
    }
    out_summary->bold_outer = (open_bold_char != 0 && open_italic_char != 0) ? bold_outer : 0u;

    return VN_OK;
}
