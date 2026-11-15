#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

namespace {
bool contains_clipboard_email(std::u16string_view text) noexcept
{
    if (text.find(u'@') == std::u16string_view::npos) return false;
    const size_t n = text.size();
    for (size_t pos = 0; pos < n; ++pos)
    {
        const char16_t ch = text[pos];
        if (!(is_regex_word_char(ch) || ch == u'.' || ch == u'+' || ch == u'-'))
            continue;
        if (!is_word_boundary(text, pos))
            continue;

        size_t cur = pos;
        while (cur < n && (is_regex_word_char(text[cur]) || text[cur] == u'.' || text[cur] == u'+' || text[cur] == u'-'))
            ++cur;
        if (cur == pos || cur >= n || text[cur] != u'@')
            continue;

        const size_t dom_start = cur + 1;
        size_t max_k = dom_start;
        while (max_k < n && (is_regex_word_char(text[max_k]) || text[max_k] == u'.' || text[max_k] == u'-'))
            ++max_k;
        if (max_k < dom_start + 4)
            continue;

        for (size_t dot = max_k - 2; dot > dom_start; --dot)
        {
            if (text[dot] == u'.')
            {
                size_t tld_end = dot + 1;
                while (tld_end < n && is_ascii_alpha(text[tld_end])) ++tld_end;
                if (tld_end - (dot + 1) >= 2 && is_word_boundary(text, tld_end))
                    return true;
            }
        }
    }
    return false;
}

bool match_icase_kw(std::u16string_view text, size_t pos, std::u16string_view kw) noexcept
{
    return pos + kw.size() <= text.size() && equals_ascii_icase(text.substr(pos, kw.size()), kw);
}

bool looks_like_clipboard_code(std::u16string_view text) noexcept
{
    const size_t n = text.size();
    if (n < 3) return false;

    // Unanchored branch: =>\s*\{
    for (size_t i = 0; i + 2 < n; ++i)
    {
        if (text[i] == u'=' && text[i + 1] == u'>')
        {
            size_t ws = i + 2;
            while (ws < n && vnotch::is_unicode_whitespace(text[ws])) ++ws;
            if (ws < n && text[ws] == u'{') return true;
        }
    }

    // Multiline line-anchored branches: (?m)^[ \t]*(...)
    size_t line_start = 0;
    while (line_start <= n)
    {
        size_t p = line_start;
        while (p < n && (text[p] == u' ' || text[p] == u'\t')) ++p;
        if (p < n)
        {
            // 1. using\s+[\w.]+;
            if (match_icase_kw(text, p, u"using") && p + 5 < n && vnotch::is_unicode_whitespace(text[p + 5]))
            {
                size_t q = p + 5;
                while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                const size_t ns_start = q;
                while (q < n && (is_regex_word_char(text[q]) || text[q] == u'.')) ++q;
                if (q > ns_start && q < n && text[q] == u';') return true;
            }

            // 2. import\s
            if (match_icase_kw(text, p, u"import") && p + 6 < n && vnotch::is_unicode_whitespace(text[p + 6]))
                return true;

            // 3. from\s+\w+\s+import
            if (match_icase_kw(text, p, u"from") && p + 4 < n && vnotch::is_unicode_whitespace(text[p + 4]))
            {
                size_t q = p + 4;
                while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                const size_t mod_start = q;
                while (q < n && is_regex_word_char(text[q])) ++q;
                if (q > mod_start && q < n && vnotch::is_unicode_whitespace(text[q]))
                {
                    while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                    if (match_icase_kw(text, q, u"import")) return true;
                }
            }

            // 4. (?:public|private|internal)\s+(?:static\s+)?(?:class|void|Task)\b
            {
                size_t vis_len = 0;
                if (match_icase_kw(text, p, u"public")) vis_len = 6;
                else if (match_icase_kw(text, p, u"private")) vis_len = 7;
                else if (match_icase_kw(text, p, u"internal")) vis_len = 8;

                if (vis_len > 0 && p + vis_len < n && vnotch::is_unicode_whitespace(text[p + vis_len]))
                {
                    size_t q = p + vis_len;
                    while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                    if (match_icase_kw(text, q, u"static") && q + 6 < n && vnotch::is_unicode_whitespace(text[q + 6]))
                    {
                        size_t after_static = q + 6;
                        while (after_static < n && vnotch::is_unicode_whitespace(text[after_static])) ++after_static;
                        q = after_static;
                    }
                    size_t kw_len = 0;
                    if (match_icase_kw(text, q, u"class")) kw_len = 5;
                    else if (match_icase_kw(text, q, u"void")) kw_len = 4;
                    else if (match_icase_kw(text, q, u"task")) kw_len = 4;
                    if (kw_len > 0 && is_word_boundary(text, q + kw_len)) return true;
                }
            }

            // 5. (?:const|let|var|int|long|float|double|decimal|bool|string|char|byte)\s+\w+\s*=
            {
                static constexpr std::u16string_view kDeclKeywords[] = {
                    u"decimal", u"double", u"string", u"const", u"float",
                    u"long", u"bool", u"char", u"byte", u"let", u"var", u"int"
                };
                for (std::u16string_view kw : kDeclKeywords)
                {
                    if (match_icase_kw(text, p, kw) && p + kw.size() < n &&
                        vnotch::is_unicode_whitespace(text[p + kw.size()]))
                    {
                        size_t q = p + kw.size();
                        while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                        const size_t id_start = q;
                        while (q < n && is_regex_word_char(text[q])) ++q;
                        if (q > id_start)
                        {
                            while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                            if (q < n && text[q] == u'=') return true;
                        }
                        break;
                    }
                }
            }

            // 6 & 7. def\s+\w+\( | function\s+\w+\(
            {
                size_t fn_len = 0;
                if (match_icase_kw(text, p, u"def")) fn_len = 3;
                else if (match_icase_kw(text, p, u"function")) fn_len = 8;
                if (fn_len > 0 && p + fn_len < n && vnotch::is_unicode_whitespace(text[p + fn_len]))
                {
                    size_t q = p + fn_len;
                    while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                    const size_t id_start = q;
                    while (q < n && is_regex_word_char(text[q])) ++q;
                    if (q > id_start && q < n && text[q] == u'(') return true;
                }
            }

            // 8. SELECT\s+.+\s+FROM\b
            if (match_icase_kw(text, p, u"select") && p + 6 < n && vnotch::is_unicode_whitespace(text[p + 6]))
            {
                size_t q = p + 6;
                while (q < n && vnotch::is_unicode_whitespace(text[q])) ++q;
                if (q < n && text[q] != u'\n')
                {
                    size_t dot_end = q + 1;
                    while (dot_end <= n)
                    {
                        if (dot_end < n && vnotch::is_unicode_whitespace(text[dot_end]))
                        {
                            size_t ws_end = dot_end;
                            while (ws_end < n && vnotch::is_unicode_whitespace(text[ws_end])) ++ws_end;
                            if (match_icase_kw(text, ws_end, u"from") && is_word_boundary(text, ws_end + 4))
                                return true;
                        }
                        if (dot_end == n || text[dot_end] == u'\n') break;
                        ++dot_end;
                    }
                }
            }

            // 9. <\/?(?:div|html|script|style|body)\b
            if (text[p] == u'<')
            {
                size_t q = p + 1;
                if (q < n && text[q] == u'/') ++q;
                static constexpr std::u16string_view kHtmlTags[] = {
                    u"script", u"style", u"html", u"body", u"div"
                };
                for (std::u16string_view tag : kHtmlTags)
                {
                    if (match_icase_kw(text, q, tag) && is_word_boundary(text, q + tag.size()))
                        return true;
                }
            }

            // 10. (?:\w+\.)*\w+\([^\r\n]*\);[ \t]*$
            if (is_regex_word_char(text[p]))
            {
                size_t q = p;
                while (true)
                {
                    const size_t seg_start = q;
                    while (q < n && is_regex_word_char(text[q])) ++q;
                    if (q == seg_start) break;
                    if (q < n && text[q] == u'.')
                    {
                        ++q;
                        continue;
                    }
                    if (q < n && text[q] == u'(')
                    {
                        size_t line_end = q + 1;
                        bool has_cr = false;
                        while (line_end < n && text[line_end] != u'\n')
                        {
                            if (text[line_end] == u'\r') has_cr = true;
                            ++line_end;
                        }
                        if (!has_cr)
                        {
                            size_t trim_end = line_end;
                            while (trim_end > q + 1 && (text[trim_end - 1] == u' ' || text[trim_end - 1] == u'\t'))
                                --trim_end;
                            if (trim_end >= q + 3 && text[trim_end - 2] == u')' && text[trim_end - 1] == u';')
                                return true;
                        }
                    }
                    break;
                }
            }
        }

        const size_t nl = text.find(u'\n', line_start);
        if (nl == std::u16string_view::npos) break;
        line_start = nl + 1;
    }

    return false;
}


}

VN_EXPORT int32_t __stdcall vn_clipboard_classify_text_traits(
    const wchar_t* text_ptr, int32_t text_len,
    uint8_t* out_has_email, uint8_t* out_looks_like_code) noexcept
{
    if ((out_has_email == nullptr && out_looks_like_code == nullptr) ||
        text_len < 0 || (text_len > 0 && text_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    const std::u16string_view text = make_u16_view(text_ptr, text_len);
    if (out_has_email != nullptr)
        *out_has_email = contains_clipboard_email(text) ? 1 : 0;
    if (out_looks_like_code != nullptr)
        *out_looks_like_code = looks_like_clipboard_code(text) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_is_public_ip_bytes(
    const uint8_t* addr_ptr, int32_t addr_len, uint8_t* out_is_public) noexcept
{
    if (out_is_public == nullptr || addr_ptr == nullptr || (addr_len != 4 && addr_len != 16))
        return VN_ERR_INVALID_ARG;
    *out_is_public = 0;

    const uint8_t* b = addr_ptr;
    int32_t len = addr_len;

    // Unwrap IPv4-mapped IPv6 (::ffff:a.b.c.d) if passed directly as 16 bytes.
    if (len == 16 &&
        b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 &&
        b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
        b[8] == 0 && b[9] == 0 && b[10] == 0xFFu && b[11] == 0xFFu)
    {
        b += 12;
        len = 4;
    }

    bool pub = false;
    if (len == 4)
    {
        pub = b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
              !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
              !(b[0] == 169 && b[1] == 254) &&
              !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
              !(b[0] == 192 && (b[1] == 168 || b[1] == 0)) &&
              !(b[0] == 198 && (b[1] == 18 || b[1] == 19));
    }
    else
    {
        pub = (b[0] & 0xE0u) == 0x20u &&
              !(b[0] == 0x20u && b[1] == 2u) &&
              !(b[0] == 0x20u && b[1] == 1u && b[2] == 0u && b[3] == 0u);
    }

    *out_is_public = pub ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_drop_is_blocked_device_path(
    const wchar_t* path_ptr, int32_t path_len, uint8_t* out_blocked) noexcept
{
    if (out_blocked == nullptr || path_len < 0 || (path_len > 0 && path_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_blocked = 0;

    const std::u16string_view raw = vnotch::trim_view(make_u16_view(path_ptr, path_len));
    if (raw.empty()) return VN_OK;

    auto is_sep = [](char16_t c) noexcept { return c == u'\\' || c == u'/'; };

    // Win32 / NT device namespace prefixes: \\.\, \\?\, \??\ (with \ or /)
    if (raw.size() >= 4 && is_sep(raw[0]))
    {
        if (is_sep(raw[1]) && (raw[2] == u'.' || raw[2] == u'?') && is_sep(raw[3]))
        {
            *out_blocked = 1;
            return VN_OK;
        }
        if (raw[1] == u'?' && raw[2] == u'?' && is_sep(raw[3]))
        {
            *out_blocked = 1;
            return VN_OK;
        }
    }

    // Check every path component after drive root for reserved DOS device names.
    size_t start = 0;
    if (raw.size() >= 2 && is_ascii_alpha(raw[0]) && raw[1] == u':')
        start = 2;

    while (start < raw.size())
    {
        while (start < raw.size() && is_sep(raw[start])) ++start;
        if (start >= raw.size()) break;
        size_t end = start;
        while (end < raw.size() && !is_sep(raw[end])) ++end;

        std::u16string_view seg = raw.substr(start, end - start);
        // Strip alternate data stream suffix and trailing dots/spaces normalized by Win32.
        const size_t colon = seg.find(u':');
        if (colon != std::u16string_view::npos) seg = seg.substr(0, colon);
        while (!seg.empty() && (seg.back() == u' ' || seg.back() == u'.'))
            seg.remove_suffix(1);
        const size_t dot = seg.find(u'.');
        if (dot != std::u16string_view::npos) seg = seg.substr(0, dot);
        while (!seg.empty() && seg.back() == u' ')
            seg.remove_suffix(1);

        if (equals_ascii_icase(seg, u"CON") ||
            equals_ascii_icase(seg, u"PRN") ||
            equals_ascii_icase(seg, u"AUX") ||
            equals_ascii_icase(seg, u"NUL") ||
            equals_ascii_icase(seg, u"CONIN$") ||
            equals_ascii_icase(seg, u"CONOUT$"))
        {
            *out_blocked = 1;
            return VN_OK;
        }

        if (seg.size() == 4 &&
            (equals_ascii_icase(seg.substr(0, 3), u"COM") ||
             equals_ascii_icase(seg.substr(0, 3), u"LPT")))
        {
            const char16_t d = seg[3];
            if ((d >= u'1' && d <= u'9') || d == 0x00B9u || d == 0x00B2u || d == 0x00B3u)
            {
                *out_blocked = 1;
                return VN_OK;
            }
        }

        start = end;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_drop_extract_html_img_src(
    const wchar_t* html_ptr, int32_t html_len,
    int32_t* out_offset, int32_t* out_length, uint8_t* out_found) noexcept
{
    if (out_offset == nullptr || out_length == nullptr || out_found == nullptr ||
        html_len < 0 || (html_len > 0 && html_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_offset = 0;
    *out_length = 0;
    *out_found = 0;

    const std::u16string_view html = make_u16_view(html_ptr, html_len);
    const size_t n = html.size();

    for (size_t i = 0; i + 4 < n; ++i)
    {
        if (html[i] != u'<' || !equals_ascii_icase(html.substr(i + 1, 3), u"img") ||
            !is_word_boundary(html, i + 4))
        {
            continue;
        }

        for (size_t k = i + 4; k < n && html[k] != u'>'; ++k)
        {
            if (k + 3 <= n && !is_regex_word_char(html[k - 1]) &&
                equals_ascii_icase(html.substr(k, 3), u"src") &&
                !is_regex_word_char(k + 3 < n ? html[k + 3] : u' '))
            {
                size_t p = k + 3;
                while (p < n && vnotch::is_unicode_whitespace(html[p])) ++p;
                if (p >= n || html[p] != u'=') continue;
                ++p;
                while (p < n && vnotch::is_unicode_whitespace(html[p])) ++p;
                if (p >= n) break;

                if (html[p] == u'"')
                {
                    const size_t val_start = p + 1;
                    size_t val_end = val_start;
                    while (val_end < n && html[val_end] != u'"') ++val_end;
                    if (val_end > val_start && val_end < n)
                    {
                        *out_offset = static_cast<int32_t>(val_start);
                        *out_length = static_cast<int32_t>(val_end - val_start);
                        *out_found = 1;
                        return VN_OK;
                    }
                }
                if (html[p] == u'\'')
                {
                    const size_t val_start = p + 1;
                    size_t val_end = val_start;
                    while (val_end < n && html[val_end] != u'\'') ++val_end;
                    if (val_end > val_start && val_end < n)
                    {
                        *out_offset = static_cast<int32_t>(val_start);
                        *out_length = static_cast<int32_t>(val_end - val_start);
                        *out_found = 1;
                        return VN_OK;
                    }
                }
                {
                    const size_t val_start = p;
                    size_t val_end = val_start;
                    while (val_end < n && !vnotch::is_unicode_whitespace(html[val_end]) && html[val_end] != u'>')
                        ++val_end;
                    if (val_end > val_start)
                    {
                        *out_offset = static_cast<int32_t>(val_start);
                        *out_length = static_cast<int32_t>(val_end - val_start);
                        *out_found = 1;
                        return VN_OK;
                    }
                }
            }
        }
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_drop_extract_source_url(
    const wchar_t* html_ptr, int32_t html_len,
    int32_t* out_offset, int32_t* out_length, uint8_t* out_found) noexcept
{
    if (out_offset == nullptr || out_length == nullptr || out_found == nullptr ||
        html_len < 0 || (html_len > 0 && html_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_offset = 0;
    *out_length = 0;
    *out_found = 0;

    const std::u16string_view html = make_u16_view(html_ptr, html_len);
    constexpr std::u16string_view kPrefix = u"SourceURL:";
    size_t line_start = 0;
    while (line_start < html.size())
    {
        if (line_start + kPrefix.size() < html.size() &&
            equals_ascii_icase(html.substr(line_start, kPrefix.size()), kPrefix))
        {
            const size_t val_start = line_start + kPrefix.size();
            size_t val_end = val_start;
            while (val_end < html.size() && html[val_end] != u'\r' && html[val_end] != u'\n')
                ++val_end;
            if (val_end > val_start)
            {
                *out_offset = static_cast<int32_t>(val_start);
                *out_length = static_cast<int32_t>(val_end - val_start);
                *out_found = 1;
                return VN_OK;
            }
        }
        const size_t nl = html.find(u'\n', line_start);
        if (nl == std::u16string_view::npos) break;
        line_start = nl + 1;
    }

    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_is_secret_filename(
    const wchar_t* path_ptr, int32_t path_len, uint8_t* out_is_secret) noexcept
{
    if (out_is_secret == nullptr || path_len < 0 || (path_len > 0 && path_ptr == nullptr))
        return VN_ERR_INVALID_ARG;
    *out_is_secret = 0;

    const std::u16string_view raw = make_u16_view(path_ptr, path_len);
    if (vnotch::trim_view(raw).empty()) return VN_OK;

    // Match .NET Path.GetFileName on Windows: drive prefix "C:" is root only at index 0..2,
    // while '\' and '/' split directory components anywhere.
    size_t root = (raw.size() >= 2 && is_ascii_alpha(raw[0]) && raw[1] == u':') ? 2u : 0u;
    size_t name_start = raw.size();
    while (name_start > root)
    {
        const char16_t c = raw[name_start - 1];
        if (c == u'\\' || c == u'/') break;
        --name_start;
    }
    const std::u16string_view file_name = raw.substr(name_start);
    if (file_name.empty()) return VN_OK;

    static constexpr std::u16string_view kSecretNames[] = {
        u"id_rsa", u"id_ed25519", u"id_ecdsa", u"id_dsa",
        u".npmrc", u".netrc", u"credentials", u".env"
    };
    for (std::u16string_view secret : kSecretNames)
    {
        if (equals_ascii_icase(file_name, secret))
        {
            *out_is_secret = 1;
            return VN_OK;
        }
    }

    const std::u16string_view ext = vnotch::get_extension(file_name);
    if (!ext.empty())
    {
        static constexpr std::u16string_view kSecretExts[] = {
            u".pem", u".key", u".pfx", u".p12", u".kdbx", u".env", u".ppk", u".ovpn"
        };
        for (std::u16string_view secret_ext : kSecretExts)
        {
            if (equals_ascii_icase(ext, secret_ext))
            {
                *out_is_secret = 1;
                return VN_OK;
            }
        }
    }

    return VN_OK;
}

