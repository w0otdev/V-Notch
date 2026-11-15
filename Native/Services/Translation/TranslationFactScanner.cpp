#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

namespace {
constexpr bool is_fact_trim_punct(char16_t ch) noexcept
{
    return ch == u'.' || ch == u',' || ch == u';' || ch == u':' ||
           ch == u')' || ch == u']' || ch == u'}' || ch == u'!' || ch == u'?';
}

constexpr bool is_url_body_char(char16_t ch) noexcept
{
    if (vnotch::is_unicode_whitespace(ch)) return false;
    if (ch == u'<' || ch == u'>' || ch == u'[' || ch == u']' ||
        ch == u'(' || ch == u')' || ch == u'*' || ch == u'`')
        return false;
    if (ch >= 0x3000u && ch <= 0x303Fu) return false;
    if (ch >= 0xFF01u && ch <= 0xFF65u) return false;
    return true;
}

constexpr bool is_sign_char(char16_t ch) noexcept
{
    return ch == u'+' || ch == u'-' || ch == 0x2212u;
}

size_t try_match_fact_at(std::u16string_view text, size_t pos, bool& out_is_entity) noexcept
{
    out_is_entity = false;
    const size_t n = text.size();
    const char16_t first = text[pos];

    // Branch 1: &(?:#[xX][0-9a-fA-F]+|#\d+|[A-Za-z][A-Za-z0-9]+);
    if (first == u'&' && pos + 3 < n)
    {
        size_t cur = pos + 1;
        if (text[cur] == u'#')
        {
            ++cur;
            if (cur < n && (text[cur] == u'x' || text[cur] == u'X'))
            {
                ++cur;
                const size_t hex_start = cur;
                while (cur < n && is_ascii_hex(text[cur])) ++cur;
                if (cur > hex_start && cur < n && text[cur] == u';')
                {
                    out_is_entity = true;
                    return (cur + 1) - pos;
                }
            }
            else
            {
                const size_t dec_start = cur;
                while (cur < n && is_nd_digit(text[cur])) ++cur;
                if (cur > dec_start && cur < n && text[cur] == u';')
                {
                    out_is_entity = true;
                    return (cur + 1) - pos;
                }
            }
        }
        else if (is_ascii_alpha(text[cur]))
        {
            ++cur;
            const size_t alnum_start = cur;
            while (cur < n && is_ascii_alnum(text[cur])) ++cur;
            if (cur > alnum_start && cur < n && text[cur] == u';')
            {
                out_is_entity = true;
                return (cur + 1) - pos;
            }
        }
    }

    // Branch 2: (?:https?://|www\.)[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]+
    if (first == u'h' || first == u'w')
    {
        size_t prefix_len = 0;
        if (text.substr(pos, 8) == u"https://") prefix_len = 8;
        else if (text.substr(pos, 7) == u"http://") prefix_len = 7;
        else if (text.substr(pos, 4) == u"www.") prefix_len = 4;

        if (prefix_len > 0)
        {
            size_t cur = pos + prefix_len;
            while (cur < n && is_url_body_char(text[cur])) ++cur;
            if (cur > pos + prefix_len) return cur - pos;
        }
    }

    // Branch 3: [\w.+-]+@[\w.-]+\.[\p{L}]{2,}
    if (is_regex_word_char(first) || first == u'.' || first == u'+' || first == u'-')
    {
        size_t cur = pos;
        while (cur < n && (is_regex_word_char(text[cur]) || text[cur] == u'.' || text[cur] == u'+' || text[cur] == u'-'))
            ++cur;
        if (cur > pos && cur < n && text[cur] == u'@')
        {
            const size_t dom_start = cur + 1;
            size_t max_k = dom_start;
            while (max_k < n && (is_regex_word_char(text[max_k]) || text[max_k] == u'.' || text[max_k] == u'-'))
                ++max_k;
            if (max_k >= dom_start + 4)
            {
                for (size_t dot = max_k - 2; dot > dom_start; --dot)
                {
                    if (text[dot] == u'.')
                    {
                        size_t tld_end = dot + 1;
                        while (tld_end < n && is_unicode_letter(text[tld_end])) ++tld_end;
                        if (tld_end - (dot + 1) >= 2)
                            return tld_end - pos;
                    }
                }
            }
        }
    }

    // Branch 4: (?:[\p{L}\p{N}-]+\.)+[A-Za-z]{2,}(?:/[^\s<>\[\]()*`\u3000-\u303f\uff01-\uff65]*)?
    if (is_unicode_letter(first) || is_unicode_number(first) || first == u'-')
    {
        size_t after_dots_stack[64];
        size_t group_count = 0;
        size_t cur = pos;
        while (cur < n)
        {
            const size_t lbl_start = cur;
            while (cur < n && (is_unicode_letter(text[cur]) || is_unicode_number(text[cur]) || text[cur] == u'-'))
                ++cur;
            if (cur == lbl_start || cur >= n || text[cur] != u'.')
                break;
            ++cur;
            if (group_count < 64)
                after_dots_stack[group_count++] = cur;
            else
                after_dots_stack[63] = cur;
        }
        for (size_t k = group_count; k > 0; --k)
        {
            const size_t tld_start = after_dots_stack[k - 1];
            size_t tld_end = tld_start;
            while (tld_end < n && is_ascii_alpha(text[tld_end])) ++tld_end;
            if (tld_end - tld_start >= 2)
            {
                size_t end = tld_end;
                if (end < n && text[end] == u'/')
                {
                    ++end;
                    while (end < n && is_url_body_char(text[end])) ++end;
                }
                return end - pos;
            }
        }
    }

    // Branch 5: [A-Z]{2,}[-_]\d+[A-Z0-9_-]*
    if (first >= u'A' && first <= u'Z')
    {
        size_t cur = pos;
        while (cur < n && text[cur] >= u'A' && text[cur] <= u'Z') ++cur;
        if (cur - pos >= 2 && cur < n && (text[cur] == u'-' || text[cur] == u'_'))
        {
            ++cur;
            const size_t dig_start = cur;
            while (cur < n && is_nd_digit(text[cur])) ++cur;
            if (cur > dig_start)
            {
                while (cur < n && ((text[cur] >= u'A' && text[cur] <= u'Z') ||
                                   (text[cur] >= u'0' && text[cur] <= u'9') ||
                                   text[cur] == u'_' || text[cur] == u'-'))
                {
                    ++cur;
                }
                return cur - pos;
            }
        }
    }

    // Branch 6: (?:[+\-−]?\p{Sc}\s*)?[+\-−]?\d+(?:[.,:/\u066B\u066C-]\d+)*(?:\s*(?:[%\p{Sc}]|元|人民币|USD|VND|EUR|CNY|JPY|GBP))?
    if (is_sign_char(first) || is_currency_symbol(first) || is_nd_digit(first))
    {
        auto try_parse_number_core = [&](size_t start_idx, size_t& out_end) noexcept -> bool
        {
            size_t c = start_idx;
            if (c < n && is_sign_char(text[c])) ++c;
            const size_t d0 = c;
            while (c < n && is_nd_digit(text[c])) ++c;
            if (c == d0) return false;

            while (c + 1 < n)
            {
                const char16_t sep = text[c];
                if ((sep == u'.' || sep == u',' || sep == u':' || sep == u'/' ||
                     sep == 0x066Bu || sep == 0x066Cu || sep == u'-') &&
                    is_nd_digit(text[c + 1]))
                {
                    c += 2;
                    while (c < n && is_nd_digit(text[c])) ++c;
                }
                else
                {
                    break;
                }
            }

            size_t ws = c;
            while (ws < n && vnotch::is_unicode_whitespace(text[ws])) ++ws;
            if (ws < n)
            {
                const char16_t u = text[ws];
                if (u == u'%' || is_currency_symbol(u) || u == 0x5143u) // 元
                {
                    c = ws + 1;
                }
                else if (text.substr(ws, 3) == u"\x4EBA\x6C11\x5143" || // 人民币
                         text.substr(ws, 3) == u"USD" ||
                         text.substr(ws, 3) == u"VND" ||
                         text.substr(ws, 3) == u"EUR" ||
                         text.substr(ws, 3) == u"CNY" ||
                         text.substr(ws, 3) == u"JPY" ||
                         text.substr(ws, 3) == u"GBP")
                {
                    c = ws + 3;
                }
            }
            out_end = c;
            return true;
        };

        size_t p = pos;
        if (p < n && is_sign_char(text[p])) ++p;
        if (p < n && is_currency_symbol(text[p]))
        {
            ++p;
            while (p < n && vnotch::is_unicode_whitespace(text[p])) ++p;
            size_t end_with_sc = 0;
            if (try_parse_number_core(p, end_with_sc))
                return end_with_sc - pos;
        }

        size_t end_plain = 0;
        if (try_parse_number_core(pos, end_plain))
            return end_plain - pos;
    }

    return 0;
}

constexpr bool has_numeric_host_suffix(std::u16string_view val) noexcept
{
    for (size_t i = 0; i + 2 < val.size(); ++i)
    {
        if (val[i] == u'.' && is_ascii_alpha(val[i + 1]) && is_ascii_alpha(val[i + 2]))
            return true;
    }
    return false;
}

constexpr bool is_numeric_fact_match(std::u16string_view text, size_t match_idx, std::u16string_view match_val) noexcept
{
    if (match_val.empty()) return false;
    const char16_t first = match_val[0];
    if (!is_nd_digit(first) && !is_sign_char(first) && !is_currency_symbol(first))
        return false;
    if (match_val.find(u'@') != std::u16string_view::npos ||
        match_val.find(u"://") != std::u16string_view::npos)
        return false;
    if (match_val.find(u'.') != std::u16string_view::npos && has_numeric_host_suffix(match_val))
        return false;
    if (match_idx == 0) return true;
    const char16_t prev = text[match_idx - 1];
    return !(is_ascii_alnum(prev) || prev == u'_');
}

constexpr bool is_idiomatic_one_match(std::u16string_view text, size_t match_idx, std::u16string_view match_val) noexcept
{
    if (match_val != u"1") return false;
    if (match_idx > 0)
    {
        const char16_t prev = text[match_idx - 1];
        if (is_unicode_letter(prev) || is_unicode_number(prev) || is_currency_symbol(prev) ||
            prev == u'_' || prev == u'.' || prev == u',' || prev == u'/' || is_sign_char(prev))
        {
            return false;
        }
    }

    size_t cur = match_idx + 1;
    const size_t ws_start = cur;
    while (cur < text.size() && (text[cur] == u' ' || text[cur] == u'\t')) ++cur;
    if (cur == ws_start || cur >= text.size()) return false;

    size_t word_end = 0;
    if (cur + 2 <= text.size() && ascii_to_lower(text[cur]) == u't' &&
        (text[cur + 1] == 0x00EDu || text[cur + 1] == 0x00CDu ||
         text[cur + 1] == 0x00FDu || text[cur + 1] == 0x00DDu))
    {
        word_end = cur + 2; // tí / tý
    }
    else if (cur + 4 <= text.size() &&
             ascii_to_lower(text[cur]) == u'c' &&
             ascii_to_lower(text[cur + 1]) == u'h' &&
             (text[cur + 2] == 0x00FAu || text[cur + 2] == 0x00DAu) &&
             ascii_to_lower(text[cur + 3]) == u't')
    {
        word_end = cur + 4; // chút
    }
    else
    {
        return false;
    }

    return word_end == text.size() || !is_regex_word_char(text[word_end]);
}

void scan_all_facts(std::u16string_view text, std::vector<VnTextFactSpan>& out_spans)
{
    out_spans.clear();
    size_t pos = 0;
    while (pos < text.size())
    {
        bool is_entity = false;
        const size_t len = try_match_fact_at(text, pos, is_entity);
        if (len == 0)
        {
            ++pos;
            continue;
        }

        const std::u16string_view match_val = text.substr(pos, len);
        size_t trim_len = len;
        while (trim_len > 0 && is_fact_trim_punct(match_val[trim_len - 1]))
            --trim_len;

        VnTextFactSpan span{};
        span.offset = static_cast<int32_t>(pos);
        span.length = static_cast<int32_t>(len);
        span.trimmed_length = static_cast<int32_t>(trim_len);
        span.is_numeric = is_numeric_fact_match(text, pos, match_val) ? 1 : 0;
        span.is_idiomatic_one = is_idiomatic_one_match(text, pos, match_val) ? 1 : 0;
        span.is_entity = is_entity ? 1 : 0;
        out_spans.push_back(span);
        pos += len;
    }
}

std::u16string normalize_numeric_fact_local(std::u16string_view fact)
{
    auto is_word = [](char16_t c) noexcept
    {
        return vn_is_letter_or_digit(c) || c == u'_';
    };
    auto is_ascii_dig = [](char16_t c) noexcept
    {
        return c >= u'0' && c <= u'9';
    };

    std::u16string out;
    out.reserve(fact.size());
    for (size_t i = 0; i < fact.size(); ++i)
    {
        const char16_t c = fact[i];
        if (c == u'.' || c == u',')
        {
            size_t d_before = 0;
            while (d_before < 4 && i > d_before && is_ascii_dig(fact[i - 1 - d_before]))
                ++d_before;
            const bool valid_before = (d_before >= 1 && d_before <= 3) &&
                                      (i == d_before || !is_word(fact[i - 1 - d_before]));
            const bool valid_after = (i + 3 < fact.size()) &&
                                     is_ascii_dig(fact[i + 1]) &&
                                     is_ascii_dig(fact[i + 2]) &&
                                     is_ascii_dig(fact[i + 3]) &&
                                     (i + 4 == fact.size() || !is_ascii_dig(fact[i + 4]));
            if (valid_before && valid_after)
                continue;
        }
        out.push_back(c);
    }
    return out;
}

}

VN_EXPORT int32_t __stdcall vn_translation_integrity_extract_facts(
    const wchar_t* text_ptr, int32_t text_len,
    VnTextFactSpan* out_spans, int32_t span_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
            return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);
        if (text.empty()) return VN_OK;

        std::vector<VnTextFactSpan> spans;
        scan_all_facts(text, spans);
        const int32_t total = static_cast<int32_t>(spans.size());
        *out_count = total;
        if (total == 0) return VN_OK;
        if (out_spans == nullptr || span_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_spans, spans.data(), static_cast<size_t>(total) * sizeof(VnTextFactSpan));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_integrity_inspect(
    const wchar_t* source_ptr, int32_t source_len,
    const wchar_t* translated_ptr, int32_t translated_len,
    uint8_t* out_literals_preserved, uint8_t* out_protected_preserved) noexcept
{
    try
    {
        if (out_literals_preserved == nullptr || out_protected_preserved == nullptr ||
            source_len < 0 || translated_len < 0)
        {
            return VN_ERR_INVALID_ARG;
        }
        *out_literals_preserved = 0;
        *out_protected_preserved = 0;

        auto collect = [](std::u16string_view text,
                          std::vector<std::u16string>& observable,
                          std::vector<std::u16string>& prot)
        {
            std::vector<VnTextFactSpan> spans;
            scan_all_facts(text, spans);
            observable.reserve(spans.size());
            prot.reserve(spans.size());
            for (const VnTextFactSpan& s : spans)
            {
                const std::u16string_view val = text.substr(
                    static_cast<size_t>(s.offset), static_cast<size_t>(s.trimmed_length));
                if (s.is_idiomatic_one == 0)
                {
                    observable.push_back(s.is_numeric != 0 ? normalize_numeric_fact_local(val) : std::u16string(val));
                }
                if (s.is_numeric == 0)
                {
                    prot.push_back(std::u16string(val));
                }
            }
            std::sort(observable.begin(), observable.end());
            std::sort(prot.begin(), prot.end());
        };

        std::vector<std::u16string> src_obs, src_prot;
        std::vector<std::u16string> dst_obs, dst_prot;
        collect(make_u16_view(source_ptr, source_len), src_obs, src_prot);
        collect(make_u16_view(translated_ptr, translated_len), dst_obs, dst_prot);

        *out_literals_preserved = (src_obs == dst_obs) ? 1 : 0;
        *out_protected_preserved = (src_prot == dst_prot) ? 1 : 0;
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_lang_split_passthrough_sentences(
    const wchar_t* text_ptr, int32_t text_len,
    VnSubTextSlice* out_slices, int32_t slice_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
            return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);

        auto is_hard_break = [](char16_t c) noexcept
        {
            return c == u'\r' || c == u'\n' || c == 0x3002u || c == 0xFF01u || c == 0xFF1Fu;
        };
        auto is_ascii_sent_end = [](char16_t c) noexcept
        {
            return c == u'.' || c == u'!' || c == u'?';
        };

        std::vector<VnSubTextSlice> slices;
        slices.reserve(8);
        size_t seg_start = 0;
        size_t i = 0;
        while (i < text.size())
        {
            if (is_hard_break(text[i]))
            {
                slices.push_back({static_cast<int32_t>(seg_start), static_cast<int32_t>(i - seg_start)});
                while (i < text.size() && is_hard_break(text[i])) ++i;
                seg_start = i;
            }
            else if (i > 0 && is_ascii_sent_end(text[i - 1]) && vnotch::is_unicode_whitespace(text[i]))
            {
                slices.push_back({static_cast<int32_t>(seg_start), static_cast<int32_t>(i - seg_start)});
                while (i < text.size() && vnotch::is_unicode_whitespace(text[i])) ++i;
                seg_start = i;
            }
            else
            {
                ++i;
            }
        }
        slices.push_back({static_cast<int32_t>(seg_start), static_cast<int32_t>(text.size() - seg_start)});

        const int32_t total = static_cast<int32_t>(slices.size());
        *out_count = total;
        if (out_slices == nullptr || slice_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_slices, slices.data(), static_cast<size_t>(total) * sizeof(VnSubTextSlice));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_structured_find_shortcodes(
    const wchar_t* text_ptr, int32_t text_len,
    VnSubTextSlice* out_slices, int32_t slice_cap, int32_t* out_count) noexcept
{
    try
    {
        if (out_count == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
            return VN_ERR_INVALID_ARG;
        *out_count = 0;
        const std::u16string_view text = make_u16_view(text_ptr, text_len);

        std::vector<VnSubTextSlice> slices;
        for (size_t i = 0; i + 2 < text.size();)
        {
            if (text[i] == u':' && (is_ascii_alpha(text[i + 1]) || text[i + 1] == u'_'))
            {
                size_t j = i + 2;
                while (j < text.size() && (is_ascii_alnum(text[j]) || text[j] == u'_' || text[j] == u'+' || text[j] == u'-'))
                    ++j;
                if (j < text.size() && text[j] == u':')
                {
                    slices.push_back({static_cast<int32_t>(i), static_cast<int32_t>((j + 1) - i)});
                    i = j + 1;
                    continue;
                }
            }
            ++i;
        }

        const int32_t total = static_cast<int32_t>(slices.size());
        *out_count = total;
        if (total == 0) return VN_OK;
        if (out_slices == nullptr || slice_cap < total) return VN_ERR_BUFFER_TOO_SMALL;
        std::memcpy(out_slices, slices.data(), static_cast<size_t>(total) * sizeof(VnSubTextSlice));
        return VN_OK;
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_translation_structured_has_letter_outside_shortcodes(
    const wchar_t* text_ptr, int32_t text_len, uint8_t* out_has_letter) noexcept
{
    if (out_has_letter == nullptr || text_len < 0 || (text_len > 0 && text_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_has_letter = 0;
    const std::u16string_view text = make_u16_view(text_ptr, text_len);

    for (size_t i = 0; i < text.size();)
    {
        if (text[i] == u':' && i + 2 < text.size() && (is_ascii_alpha(text[i + 1]) || text[i + 1] == u'_'))
        {
            size_t j = i + 2;
            while (j < text.size() && (is_ascii_alnum(text[j]) || text[j] == u'_' || text[j] == u'+' || text[j] == u'-'))
                ++j;
            if (j < text.size() && text[j] == u':')
            {
                i = j + 1;
                continue;
            }
        }
        if (is_unicode_letter(text[i]))
        {
            *out_has_letter = 1;
            return VN_OK;
        }
        ++i;
    }
    return VN_OK;
}

