#include "../vnotch_native.h"

namespace
{
    constexpr bool is_space(char16_t ch) noexcept
    {
        return ch == u' ' || ch == u'\t' || ch == u'\r' || ch == u'\n';
    }

    constexpr bool is_digit(char16_t ch) noexcept
    {
        return ch >= u'0' && ch <= u'9';
    }

    constexpr bool is_alpha(char16_t ch) noexcept
    {
        return (ch >= u'a' && ch <= u'z') || (ch >= u'A' && ch <= u'Z');
    }

    constexpr bool is_word_char(char16_t ch) noexcept
    {
        return is_alpha(ch) || is_digit(ch) || ch == u'_';
    }

    constexpr char16_t to_lower_ascii(char16_t ch) noexcept
    {
        return (ch >= u'A' && ch <= u'Z') ? static_cast<char16_t>(ch + 32) : ch;
    }

    constexpr bool is_url_terminator(char16_t ch) noexcept
    {
        return is_space(ch) || ch == u'<' || ch == u'>' || ch == u'"' ||
               ch == u'\'' || ch == u'`' || ch == u')' || ch == u']';
    }

    constexpr bool is_trailing_url_punct(char16_t ch) noexcept
    {
        return ch == u'.' || ch == u',' || ch == u';' || ch == u':' || ch == u'!' || ch == u'?';
    }

    constexpr bool is_invalid_inside_markdown_url(char16_t ch) noexcept
    {
        return is_space(ch) || ch == u'(' || ch == u'[' || ch == u']';
    }

    constexpr bool starts_with_http_scheme(const char16_t* text, int32_t text_len, int32_t pos, int32_t* out_scheme_len) noexcept
    {
        if (pos + 8 <= text_len &&
            to_lower_ascii(text[pos]) == u'h' &&
            to_lower_ascii(text[pos + 1]) == u't' &&
            to_lower_ascii(text[pos + 2]) == u't' &&
            to_lower_ascii(text[pos + 3]) == u'p')
        {
            if (to_lower_ascii(text[pos + 4]) == u's' &&
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
        else if (pos + 7 <= text_len &&
                 to_lower_ascii(text[pos]) == u'h' &&
                 to_lower_ascii(text[pos + 1]) == u't' &&
                 to_lower_ascii(text[pos + 2]) == u't' &&
                 to_lower_ascii(text[pos + 3]) == u'p' &&
                 text[pos + 4] == u':' &&
                 text[pos + 5] == u'/' &&
                 text[pos + 6] == u'/')
        {
            *out_scheme_len = 7;
            return true;
        }
        return false;
    }

    constexpr void classify_line_slice(
        const char16_t* text_ptr,
        int32_t line_start,
        int32_t line_len,
        VnMarkdownBlockToken* out_token) noexcept
    {
        while (line_len > 0 &&
               (text_ptr[line_start + line_len - 1] == u'\r' ||
                text_ptr[line_start + line_len - 1] == u'\n'))
        {
            --line_len;
        }

        const uint8_t hard_break = (line_len >= 2 &&
                                    text_ptr[line_start + line_len - 1] == u' ' &&
                                    text_ptr[line_start + line_len - 2] == u' ')
                                       ? 1
                                       : 0;

        int32_t trim_start = line_start;
        int32_t trim_end = line_start + line_len;
        while (trim_start < trim_end && is_space(text_ptr[trim_start]))
        {
            ++trim_start;
        }
        while (trim_end > trim_start && is_space(text_ptr[trim_end - 1]))
        {
            --trim_end;
        }

        const int32_t trim_len = trim_end - trim_start;
        if (trim_len <= 0)
        {
            *out_token = { VN_MD_BLOCK_EMPTY, 0, 0, hard_break, trim_start, 0, trim_start, 0 };
            return;
        }

        const char16_t first = text_ptr[trim_start];

        // 1. Horizontal Rule: ---, ***, ___ (with optional spaces)
        if ((first == u'-' || first == u'*' || first == u'_') && trim_len >= 3)
        {
            bool all_rule = true;
            int32_t rule_char_count = 0;
            for (int32_t k = trim_start; k < trim_end; ++k)
            {
                const char16_t ch = text_ptr[k];
                if (ch == first)
                {
                    ++rule_char_count;
                }
                else if (ch != u' ' && ch != u'\t')
                {
                    all_rule = false;
                    break;
                }
            }

            if (all_rule && rule_char_count >= 3)
            {
                *out_token = { VN_MD_BLOCK_HORIZONTAL_RULE, 0, 0, hard_break, trim_start, trim_len, trim_start, trim_len };
                return;
            }
        }

        // 2. Heading: #, ##, ###, ####, #####, ######
        if (first == u'#')
        {
            int32_t hashes = 0;
            while (hashes < trim_len && hashes < 6 && text_ptr[trim_start + hashes] == u'#')
            {
                ++hashes;
            }

            if (hashes >= 1 && hashes < trim_len &&
                (is_space(text_ptr[trim_start + hashes]) || !is_digit(text_ptr[trim_start + hashes])))
            {
                int32_t c_start = trim_start + hashes;
                while (c_start < trim_end && is_space(text_ptr[c_start]))
                {
                    ++c_start;
                }
                if (trim_end > c_start)
                {
                    *out_token = {
                        VN_MD_BLOCK_HEADING,
                        static_cast<uint8_t>(hashes),
                        0,
                        hard_break,
                        c_start,
                        trim_end - c_start,
                        trim_start,
                        hashes
                    };
                    return;
                }
            }
        }

        // 3. Table Row / Table Separator: | ... |
        if (first == u'|' && trim_len >= 2)
        {
            bool has_second_pipe = false;
            bool only_sep_chars = true;
            int32_t dash_count = 0;
            for (int32_t k = trim_start + 1; k < trim_end; ++k)
            {
                const char16_t ch = text_ptr[k];
                if (ch == u'|')
                {
                    has_second_pipe = true;
                }
                else if (ch == u'-')
                {
                    ++dash_count;
                }
                else if (ch != u':' && ch != u' ' && ch != u'\t')
                {
                    only_sep_chars = false;
                }
            }

            if (has_second_pipe)
            {
                const uint8_t kind = (only_sep_chars && dash_count >= 3)
                                         ? VN_MD_BLOCK_TABLE_SEPARATOR
                                         : VN_MD_BLOCK_TABLE_ROW;
                *out_token = { kind, 0, 0, hard_break, trim_start, trim_len, trim_start, 1 };
                return;
            }
        }

        // 4. Blockquote: > ...
        if (first == u'>')
        {
            int32_t quotes = 0;
            while (quotes < trim_len && text_ptr[trim_start + quotes] == u'>')
            {
                ++quotes;
            }
            int32_t c_start = trim_start + quotes;
            while (c_start < trim_end && is_space(text_ptr[c_start]))
            {
                ++c_start;
            }
            const uint8_t q_level = static_cast<uint8_t>(quotes > 6 ? 6 : quotes);
            *out_token = { VN_MD_BLOCK_BLOCKQUOTE, q_level, 0, hard_break, c_start, trim_end - c_start, trim_start, quotes };
            return;
        }

        // 5. Checklist Item (- [ ], - [x]) and Bullet Item (- , * , • , + )
        if ((first == u'-' || first == u'*' || first == u'+' || first == u'\u2022') &&
            trim_len >= 2 && is_space(text_ptr[trim_start + 1]))
        {
            int32_t p = trim_start + 1;
            while (p < trim_end && is_space(text_ptr[p]))
            {
                ++p;
            }

            if (p + 3 <= trim_end &&
                text_ptr[p] == u'[' &&
                (text_ptr[p + 1] == u' ' || text_ptr[p + 1] == u'x' || text_ptr[p + 1] == u'X') &&
                text_ptr[p + 2] == u']' &&
                (p + 3 == trim_end || is_space(text_ptr[p + 3])))
            {
                const uint8_t is_checked = (text_ptr[p + 1] == u'x' || text_ptr[p + 1] == u'X') ? 1 : 0;
                int32_t c_start = p + 3;
                while (c_start < trim_end && is_space(text_ptr[c_start]))
                {
                    ++c_start;
                }
                *out_token = {
                    VN_MD_BLOCK_CHECKLIST_ITEM,
                    1,
                    is_checked,
                    hard_break,
                    c_start,
                    trim_end - c_start,
                    trim_start,
                    (p + 3) - trim_start
                };
                return;
            }

            *out_token = {
                VN_MD_BLOCK_BULLET_ITEM,
                1,
                0,
                hard_break,
                p,
                trim_end - p,
                trim_start,
                1
            };
            return;
        }

        // 6. Numbered Item: 1. ... or 2) ...
        if (is_digit(first))
        {
            int32_t d_end = trim_start;
            uint32_t num_val = 0;
            while (d_end < trim_end && is_digit(text_ptr[d_end]))
            {
                num_val = num_val * 10u + static_cast<uint32_t>(text_ptr[d_end] - u'0');
                if (num_val > 255u)
                {
                    num_val = 255u;
                }
                ++d_end;
            }

            if (d_end > trim_start && d_end + 1 < trim_end &&
                (text_ptr[d_end] == u'.' || text_ptr[d_end] == u')') &&
                is_space(text_ptr[d_end + 1]))
            {
                int32_t c_start = d_end + 1;
                while (c_start < trim_end && is_space(text_ptr[c_start]))
                {
                    ++c_start;
                }
                *out_token = {
                    VN_MD_BLOCK_NUMBERED_ITEM,
                    static_cast<uint8_t>(num_val),
                    0,
                    hard_break,
                    c_start,
                    trim_end - c_start,
                    trim_start,
                    (d_end + 1) - trim_start
                };
                return;
            }
        }

        // 7. Default Paragraph
        *out_token = { VN_MD_BLOCK_PARAGRAPH, 0, 0, hard_break, trim_start, trim_len, trim_start, 0 };
    }
}

VN_EXPORT int32_t __stdcall vn_markdown_classify_line(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownBlockToken* out_token) noexcept
{
    if (line_len < 0 || (line_len > 0 && line_ptr == nullptr) || out_token == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    if (line_len == 0)
    {
        *out_token = { VN_MD_BLOCK_EMPTY, 0, 0, 0, 0, 0, 0, 0 };
        return VN_OK;
    }

    classify_line_slice(line_ptr, 0, line_len, out_token);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_markdown_tokenize_blocks(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownBlockToken* out_tokens,
    int32_t token_cap,
    int32_t* out_written) noexcept
{
    if (text_len < 0 || (text_len > 0 && text_ptr == nullptr) ||
        token_cap < 0 || (token_cap > 0 && out_tokens == nullptr) ||
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
    int32_t pos = 0;

    while (pos <= text_len)
    {
        int32_t line_end = pos;
        while (line_end < text_len && text_ptr[line_end] != u'\r' && text_ptr[line_end] != u'\n')
        {
            ++line_end;
        }

        VnMarkdownBlockToken tok{};
        classify_line_slice(text_ptr, pos, line_end - pos, &tok);

        if (out_tokens != nullptr && count < token_cap)
        {
            out_tokens[count] = tok;
        }
        ++count;

        if (line_end >= text_len)
        {
            break;
        }

        if (text_ptr[line_end] == u'\r' && line_end + 1 < text_len && text_ptr[line_end + 1] == u'\n')
        {
            pos = line_end + 2;
        }
        else
        {
            pos = line_end + 1;
        }

        if (pos == text_len)
        {
            // Trailing newline produces one final empty line just like string.Split
            VnMarkdownBlockToken empty_tok{ VN_MD_BLOCK_EMPTY, 0, 0, 0, pos, 0, pos, 0 };
            if (out_tokens != nullptr && count < token_cap)
            {
                out_tokens[count] = empty_tok;
            }
            ++count;
            break;
        }
    }

    *out_written = count;
    if (out_tokens != nullptr && count > token_cap)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_markdown_split_table_cells(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownSlice* out_cells,
    int32_t cell_cap,
    int32_t* out_written) noexcept
{
    if (line_len < 0 || (line_len > 0 && line_ptr == nullptr) ||
        cell_cap < 0 || (cell_cap > 0 && out_cells == nullptr) ||
        out_written == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_written = 0;
    if (line_len == 0)
    {
        return VN_OK;
    }

    int32_t trim_start = 0;
    int32_t trim_end = line_len;
    while (trim_start < trim_end && is_space(line_ptr[trim_start]))
    {
        ++trim_start;
    }
    while (trim_end > trim_start && is_space(line_ptr[trim_end - 1]))
    {
        --trim_end;
    }

    if (trim_start < trim_end && line_ptr[trim_start] == u'|')
    {
        ++trim_start;
    }
    if (trim_end > trim_start && line_ptr[trim_end - 1] == u'|')
    {
        --trim_end;
    }

    if (trim_start >= trim_end)
    {
        return VN_OK;
    }

    int32_t count = 0;
    int32_t cell_start = trim_start;
    bool in_code = false;

    for (int32_t i = trim_start; i <= trim_end; ++i)
    {
        const bool at_end = (i == trim_end);
        const char16_t ch = at_end ? u'|' : line_ptr[i];

        if (!at_end && ch == u'`')
        {
            in_code = !in_code;
            continue;
        }

        if (at_end || (ch == u'|' && !in_code && (i == trim_start || line_ptr[i - 1] != u'\\')))
        {
            int32_t c_start = cell_start;
            int32_t c_end = i;
            while (c_start < c_end && is_space(line_ptr[c_start]))
            {
                ++c_start;
            }
            while (c_end > c_start && is_space(line_ptr[c_end - 1]))
            {
                --c_end;
            }

            if (out_cells != nullptr && count < cell_cap)
            {
                out_cells[count] = { c_start, c_end - c_start };
            }
            ++count;
            cell_start = i + 1;
        }
    }

    *out_written = count;
    if (out_cells != nullptr && count > cell_cap)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_markdown_tokenize_inlines(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownInlineToken* out_tokens,
    int32_t token_cap,
    int32_t* out_written) noexcept
{
    if (text_len < 0 || (text_len > 0 && text_ptr == nullptr) ||
        token_cap < 0 || (token_cap > 0 && out_tokens == nullptr) ||
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
    auto emit_token = [&](uint8_t kind, int32_t t_start, int32_t t_len, int32_t a_start, int32_t a_len) noexcept
    {
        if (out_tokens != nullptr && count < token_cap)
        {
            out_tokens[count] = { kind, t_start, t_len, a_start, a_len };
        }
        ++count;
    };

    // Monotonic lookahead cursors guarantee strict O(N) time with zero backtracking.
    int32_t next_tick = 0;
    int32_t next_dstar = 0;
    int32_t next_star = 0;
    int32_t next_lbracket = 0;
    int32_t next_rbracket = 0;
    int32_t next_rparen = 0;
    int32_t next_url_bad = 0;

    int32_t plain_start = 0;
    int32_t i = 0;

    while (i < text_len)
    {
        const char16_t ch = text_ptr[i];

        // 1. Inline code: `...`
        if (ch == u'`')
        {
            if (i + 1 < text_len && text_ptr[i + 1] != u'`')
            {
                if (next_tick < i + 1)
                {
                    next_tick = i + 1;
                }
                while (next_tick < text_len && text_ptr[next_tick] != u'`')
                {
                    ++next_tick;
                }
                if (next_tick < text_len && next_tick > i + 1)
                {
                    if (i > plain_start)
                    {
                        emit_token(VN_MD_INLINE_TEXT, plain_start, i - plain_start, 0, 0);
                    }
                    emit_token(VN_MD_INLINE_CODE, i + 1, next_tick - (i + 1), 0, 0);
                    i = next_tick + 1;
                    plain_start = i;
                    continue;
                }
            }
            else if (i + 1 < text_len && text_ptr[i + 1] == u'`')
            {
                // Skip contiguous backtick run in one pass
                int32_t run_end = i + 2;
                while (run_end < text_len && text_ptr[run_end] == u'`')
                {
                    ++run_end;
                }
                i = run_end;
                continue;
            }
            ++i;
            continue;
        }

        // 2. Bold (**...**) or star runs (***...)
        if (ch == u'*' && i + 1 < text_len && text_ptr[i + 1] == u'*')
        {
            if (i + 2 < text_len && text_ptr[i + 2] == u'*')
            {
                // Run of 3+ stars: consume entire star run in O(run) without backtracking
                int32_t run_end = i + 3;
                while (run_end < text_len && text_ptr[run_end] == u'*')
                {
                    ++run_end;
                }
                i = run_end;
                continue;
            }

            if (next_dstar < i + 2)
            {
                next_dstar = i + 2;
            }
            while (next_dstar + 1 < text_len &&
                   !(text_ptr[next_dstar] == u'*' && text_ptr[next_dstar + 1] == u'*'))
            {
                ++next_dstar;
            }

            if (next_dstar + 1 < text_len && next_dstar > i + 2)
            {
                if (i > plain_start)
                {
                    emit_token(VN_MD_INLINE_TEXT, plain_start, i - plain_start, 0, 0);
                }
                emit_token(VN_MD_INLINE_BOLD, i + 2, next_dstar - (i + 2), 0, 0);
                i = next_dstar + 2;
                plain_start = i;
                continue;
            }

            i += 2;
            continue;
        }

        // 3. Italic (*...*)
        if (ch == u'*')
        {
            const bool valid_open = (i == 0 || !is_word_char(text_ptr[i - 1])) &&
                                    (i + 1 < text_len) &&
                                    !is_space(text_ptr[i + 1]) &&
                                    text_ptr[i + 1] != u'*';
            if (valid_open)
            {
                if (next_star < i + 1)
                {
                    next_star = i + 1;
                }
                while (next_star < text_len && text_ptr[next_star] != u'*')
                {
                    ++next_star;
                }

                if (next_star < text_len && next_star > i + 1)
                {
                    const int32_t j = next_star;
                    const bool valid_close = !is_space(text_ptr[j - 1]) &&
                                             (j + 1 == text_len || (!is_word_char(text_ptr[j + 1]) && text_ptr[j + 1] != u'*'));
                    if (valid_close)
                    {
                        if (i > plain_start)
                        {
                            emit_token(VN_MD_INLINE_TEXT, plain_start, i - plain_start, 0, 0);
                        }
                        emit_token(VN_MD_INLINE_ITALIC, i + 1, j - (i + 1), 0, 0);
                        i = j + 1;
                        plain_start = i;
                        continue;
                    }
                }
            }
            ++i;
            continue;
        }

        // 4. Markdown Link: [label](url)
        if (ch == u'[')
        {
            if (next_rbracket < i + 1)
            {
                next_rbracket = i + 1;
            }
            while (next_rbracket < text_len && text_ptr[next_rbracket] != u']')
            {
                ++next_rbracket;
            }

            if (next_lbracket < i + 1)
            {
                next_lbracket = i + 1;
            }
            while (next_lbracket < text_len && text_ptr[next_lbracket] != u'[')
            {
                ++next_lbracket;
            }

            // Ensure non-empty label without nested '[' before ']' and followed immediately by '('
            if (next_rbracket < text_len &&
                next_rbracket > i + 1 &&
                next_lbracket > next_rbracket &&
                next_rbracket + 2 < text_len &&
                text_ptr[next_rbracket + 1] == u'(')
            {
                const int32_t url_open = next_rbracket + 2;
                if (next_rparen < url_open)
                {
                    next_rparen = url_open;
                }
                while (next_rparen < text_len && text_ptr[next_rparen] != u')')
                {
                    ++next_rparen;
                }

                if (next_url_bad < url_open)
                {
                    next_url_bad = url_open;
                }
                while (next_url_bad < text_len && !is_invalid_inside_markdown_url(text_ptr[next_url_bad]))
                {
                    ++next_url_bad;
                }

                if (next_rparen < text_len && next_rparen > url_open && next_url_bad >= next_rparen)
                {
                    if (i > plain_start)
                    {
                        emit_token(VN_MD_INLINE_TEXT, plain_start, i - plain_start, 0, 0);
                    }
                    emit_token(
                        VN_MD_INLINE_LINK,
                        i + 1,
                        next_rbracket - (i + 1),
                        url_open,
                        next_rparen - url_open);
                    i = next_rparen + 1;
                    plain_start = i;
                    continue;
                }
            }
            ++i;
            continue;
        }

        // 5. Bare URL: https://... or http://...
        if ((ch == u'h' || ch == u'H') && (i == 0 || !is_word_char(text_ptr[i - 1])))
        {
            int32_t scheme_len = 0;
            if (starts_with_http_scheme(text_ptr, text_len, i, &scheme_len))
            {
                int32_t j = i + scheme_len;
                while (j < text_len && !is_url_terminator(text_ptr[j]))
                {
                    ++j;
                }
                while (j > i + scheme_len && is_trailing_url_punct(text_ptr[j - 1]))
                {
                    --j;
                }
                if (j > i + scheme_len)
                {
                    if (i > plain_start)
                    {
                        emit_token(VN_MD_INLINE_TEXT, plain_start, i - plain_start, 0, 0);
                    }
                    emit_token(VN_MD_INLINE_URL, i, j - i, i, j - i);
                    i = j;
                    plain_start = i;
                    continue;
                }
            }
        }

        ++i;
    }

    if (text_len > plain_start)
    {
        emit_token(VN_MD_INLINE_TEXT, plain_start, text_len - plain_start, 0, 0);
    }

    *out_written = count;
    if (out_tokens != nullptr && count > token_cap)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_OK;
}

#ifndef VN_MARKDOWN_EXTENDED_DEFINED
#define VN_MARKDOWN_EXTENDED_DEFINED
#pragma pack(push, 1)
struct VnMarkdownCodeFenceHeader
{
    uint8_t is_fence;
    uint8_t fence_char;
    uint8_t fence_count;
    uint8_t reserved;
    int32_t lang_start;
    int32_t lang_length;
};

struct VnMarkdownListPrefix
{
    uint8_t list_kind;
    uint8_t is_checked;
    uint16_t ordered_number;
    int32_t prefix_start;
    int32_t prefix_length;
    int32_t content_start;
    int32_t content_length;
};

struct VnMarkdownSpanBoundarySummary
{
    uint8_t has_unclosed_fence;
    uint8_t has_unclosed_inline_code;
    uint8_t has_unclosed_bold;
    uint8_t has_unclosed_italic;
    int32_t fence_count;
    int32_t inline_code_count;
    int32_t bold_marker_count;
    int32_t italic_marker_count;
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

extern "C" VN_EXPORT int32_t __stdcall vn_markdown_parse_code_fence_header(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownCodeFenceHeader* out_header) noexcept
{
    if (line_len < 0 || (line_len > 0 && line_ptr == nullptr) || out_header == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_header = { 0, 0, 0, 0, 0, 0 };
    int32_t start = 0;
    int32_t end = line_len;
    while (end > 0 && (line_ptr[end - 1] == u'\r' || line_ptr[end - 1] == u'\n'))
    {
        --end;
    }
    while (start < end && (line_ptr[start] == u' ' || line_ptr[start] == u'\t'))
    {
        ++start;
    }

    if (end - start < 3)
    {
        return VN_OK;
    }

    const char16_t fc = line_ptr[start];
    if (fc != u'`' && fc != u'~')
    {
        return VN_OK;
    }

    int32_t f_end = start;
    while (f_end < end && line_ptr[f_end] == fc)
    {
        ++f_end;
    }

    const int32_t f_count = f_end - start;
    if (f_count < 3)
    {
        return VN_OK;
    }

    int32_t lang_start = f_end;
    while (lang_start < end && is_space(line_ptr[lang_start]))
    {
        ++lang_start;
    }

    int32_t lang_end = lang_start;
    while (lang_end < end && !is_space(line_ptr[lang_end]) && line_ptr[lang_end] != u'`' && line_ptr[lang_end] != u'~')
    {
        ++lang_end;
    }

    out_header->is_fence = 1;
    out_header->fence_char = static_cast<uint8_t>(fc);
    out_header->fence_count = static_cast<uint8_t>(f_count > 255 ? 255 : f_count);
    out_header->lang_start = lang_start;
    out_header->lang_length = lang_end - lang_start;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_markdown_strip_list_prefix(
    const char16_t* line_ptr,
    int32_t line_len,
    VnMarkdownListPrefix* out_prefix) noexcept
{
    if (line_len < 0 || (line_len > 0 && line_ptr == nullptr) || out_prefix == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_prefix = { 0, 0, 0, 0, 0, 0, line_len };
    if (line_len == 0)
    {
        return VN_OK;
    }

    int32_t trim_start = 0;
    int32_t trim_end = line_len;
    while (trim_end > 0 && (line_ptr[trim_end - 1] == u'\r' || line_ptr[trim_end - 1] == u'\n'))
    {
        --trim_end;
    }
    while (trim_start < trim_end && is_space(line_ptr[trim_start]))
    {
        ++trim_start;
    }
    while (trim_end > trim_start && is_space(line_ptr[trim_end - 1]))
    {
        --trim_end;
    }

    const int32_t trim_len = trim_end - trim_start;
    if (trim_len <= 0)
    {
        out_prefix->content_start = trim_start;
        out_prefix->content_length = 0;
        return VN_OK;
    }

    const char16_t first = line_ptr[trim_start];
    if ((first == u'-' || first == u'*' || first == u'+' || first == u'\u2022') &&
        trim_len >= 2 && is_space(line_ptr[trim_start + 1]))
    {
        int32_t p = trim_start + 1;
        while (p < trim_end && is_space(line_ptr[p]))
        {
            ++p;
        }
        if (p + 3 <= trim_end &&
            line_ptr[p] == u'[' &&
            (line_ptr[p + 1] == u' ' || line_ptr[p + 1] == u'x' || line_ptr[p + 1] == u'X') &&
            line_ptr[p + 2] == u']' &&
            (p + 3 == trim_end || is_space(line_ptr[p + 3])))
        {
            const uint8_t checked = (line_ptr[p + 1] == u'x' || line_ptr[p + 1] == u'X') ? 1u : 0u;
            int32_t c_start = p + 3;
            while (c_start < trim_end && is_space(line_ptr[c_start]))
            {
                ++c_start;
            }
            *out_prefix = { 3, checked, 0, trim_start, (p + 3) - trim_start, c_start, trim_end - c_start };
            return VN_OK;
        }

        *out_prefix = { 1, 0, 0, trim_start, 1, p, trim_end - p };
        return VN_OK;
    }

    if (is_digit(first))
    {
        int32_t d_end = trim_start;
        uint32_t num_val = 0;
        while (d_end < trim_end && is_digit(line_ptr[d_end]))
        {
            num_val = num_val * 10u + static_cast<uint32_t>(line_ptr[d_end] - u'0');
            if (num_val > 65535u)
            {
                num_val = 65535u;
            }
            ++d_end;
        }

        if (d_end == trim_end)
        {
            *out_prefix = { 2, 0, static_cast<uint16_t>(num_val), trim_start, trim_len, trim_end, 0 };
            return VN_OK;
        }

        if (d_end > trim_start && d_end + 1 < trim_end &&
            (line_ptr[d_end] == u'.' || line_ptr[d_end] == u')') &&
            is_space(line_ptr[d_end + 1]))
        {
            int32_t c_start = d_end + 1;
            while (c_start < trim_end && is_space(line_ptr[c_start]))
            {
                ++c_start;
            }
            *out_prefix = {
                2,
                0,
                static_cast<uint16_t>(num_val),
                trim_start,
                (d_end + 1) - trim_start,
                c_start,
                trim_end - c_start
            };
            return VN_OK;
        }
    }

    out_prefix->content_start = trim_start;
    out_prefix->content_length = trim_len;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_markdown_scan_span_boundaries(
    const char16_t* text_ptr,
    int32_t text_len,
    VnMarkdownSpanBoundarySummary* out_summary,
    VnMarkdownSlice* out_code_spans,
    int32_t code_span_cap,
    int32_t* out_code_span_count) noexcept
{
    if (text_len < 0 || (text_len > 0 && text_ptr == nullptr) ||
        out_summary == nullptr ||
        code_span_cap < 0 || (code_span_cap > 0 && out_code_spans == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_summary = { 0, 0, 0, 0, 0, 0, 0, 0 };
    if (out_code_span_count != nullptr)
    {
        *out_code_span_count = 0;
    }
    if (text_len == 0)
    {
        return VN_OK;
    }

    int32_t fence_count = 0;
    int32_t pos = 0;
    while (pos < text_len)
    {
        int32_t line_start = pos;
        while (pos < text_len && text_ptr[pos] != u'\n')
        {
            ++pos;
        }
        int32_t p = line_start;
        while (p < pos && is_unicode_ws(text_ptr[p]))
        {
            ++p;
        }
        if (p + 3 <= pos && text_ptr[p] == u'`' && text_ptr[p + 1] == u'`' && text_ptr[p + 2] == u'`')
        {
            ++fence_count;
        }
        if (pos < text_len && text_ptr[pos] == u'\n')
        {
            ++pos;
        }
    }

    out_summary->fence_count = fence_count;
    out_summary->has_unclosed_fence = (fence_count & 1) != 0 ? 1u : 0u;
    if (out_summary->has_unclosed_fence != 0)
    {
        return VN_OK;
    }

    int32_t code_span_count = 0;
    int32_t inline_code_count = 0;
    int32_t bold_count = 0;
    int32_t italic_count = 0;
    bool unclosed_inline_code = false;

    int32_t i = 0;
    while (i < text_len)
    {
        if (i + 3 <= text_len && text_ptr[i] == u'`' && text_ptr[i + 1] == u'`' && text_ptr[i + 2] == u'`')
        {
            int32_t close = i + 3;
            bool found = false;
            while (close + 3 <= text_len)
            {
                if (text_ptr[close] == u'`' && text_ptr[close + 1] == u'`' && text_ptr[close + 2] == u'`')
                {
                    found = true;
                    break;
                }
                ++close;
            }
            if (found)
            {
                if (out_code_spans != nullptr && code_span_count < code_span_cap)
                {
                    out_code_spans[code_span_count] = { i, (close + 3) - i };
                }
                ++code_span_count;
                i = close + 3;
                continue;
            }
        }

        if (text_ptr[i] == u'`')
        {
            int32_t close = i + 1;
            while (close < text_len && text_ptr[close] != u'`')
            {
                ++close;
            }
            if (close < text_len)
            {
                if (out_code_spans != nullptr && code_span_count < code_span_cap)
                {
                    out_code_spans[code_span_count] = { i, (close + 1) - i };
                }
                ++code_span_count;
                ++inline_code_count;
                i = close + 1;
                continue;
            }
            unclosed_inline_code = true;
            ++inline_code_count;
            ++i;
            continue;
        }

        if (text_ptr[i] == u'*' && (i == 0 || text_ptr[i - 1] != u'\\'))
        {
            if (i + 1 < text_len && text_ptr[i + 1] == u'*')
            {
                ++bold_count;
                i += 2;
                continue;
            }
            ++italic_count;
        }

        ++i;
    }

    out_summary->inline_code_count = inline_code_count;
    out_summary->bold_marker_count = bold_count;
    out_summary->italic_marker_count = italic_count;
    out_summary->has_unclosed_inline_code = unclosed_inline_code ? 1u : 0u;
    out_summary->has_unclosed_bold = (!unclosed_inline_code && (bold_count & 1) != 0) ? 1u : 0u;
    out_summary->has_unclosed_italic = (!unclosed_inline_code && (italic_count & 1) != 0) ? 1u : 0u;

    if (out_code_span_count != nullptr)
    {
        *out_code_span_count = code_span_count;
    }
    if (out_code_spans != nullptr && code_span_count > code_span_cap)
    {
        return VN_ERR_BUFFER_TOO_SMALL;
    }
    return VN_OK;
}
