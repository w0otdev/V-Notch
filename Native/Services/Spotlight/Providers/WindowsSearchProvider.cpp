#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include "vnotch_common.h"
#include <algorithm>
#include <string>

using namespace vnotch;

namespace vnotch::services
{
    class WindowsSearchProvider final
    {
    public:
        static constexpr bool is_ascii_alpha(char16_t c) noexcept
        {
            return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z');
        }

        static constexpr bool is_ascii_digit(char16_t c) noexcept
        {
            return c >= u'0' && c <= u'9';
        }

        static constexpr int hex_val(char16_t c) noexcept
        {
            if (c >= u'0' && c <= u'9') return c - u'0';
            if (c >= u'a' && c <= u'f') return 10 + (c - u'a');
            if (c >= u'A' && c <= u'F') return 10 + (c - u'A');
            return -1;
        }

        static std::u16string sanitize_query_str(std::u16string_view q)
        {
            std::u16string filtered;
            filtered.reserve(std::min<size_t>(q.size(), 256));
            for (char16_t c : q)
            {
                if (c < 0x20 || c == u'"' || c == u'\'' || c == u'\\' || c == u'(' || c == u')' || c == u':' || c == u'*')
                    continue;
                filtered.push_back(c);
                if (filtered.size() == 256)
                {
                    if ((filtered.back() & 0xFC00) == 0xD800) filtered.pop_back();
                    break;
                }
            }
            return std::u16string(vnotch::trim(filtered));
        }

        static std::u16string escape_like_pattern_impl(std::u16string_view s)
        {
            std::u16string out;
            out.reserve(s.size() + 8);
            for (char16_t c : s)
            {
                if (c == u'%') out.append(u"[%]");
                else if (c == u'_') out.append(u"[_]");
                else if (c == u'[') out.append(u"[[]");
                else out.push_back(c);
            }
            return out;
        }

        static std::u16string build_name_predicate_str(std::u16string_view sanitized)
        {
            std::u16string like = escape_like_pattern_impl(sanitized);

            std::u16string contains;
            size_t i = 0;
            bool first = true;
            while (i < sanitized.size())
            {
                while (i < sanitized.size() && sanitized[i] == u' ') ++i;
                if (i >= sanitized.size()) break;
                size_t start = i;
                while (i < sanitized.size() && sanitized[i] != u' ') ++i;
                if (!first) contains.append(u" AND ");
                first = false;
                contains.push_back(u'"');
                contains.append(sanitized.substr(start, i - start));
                contains.append(u"*\"");
            }

            std::u16string out = u"System.FileName LIKE '%";
            out.append(like);
            out.append(u"%' OR CONTAINS(System.FileName, '");
            out.append(contains);
            out.append(u"')");
            return out;
        }

        static bool sanitize_scope_clause(std::u16string_view raw_scope, std::u16string& out_scope)
        {
            out_scope.clear();
            for (char16_t c : raw_scope)
            {
                if (c < 0x20) return false;
            }

            std::u16string_view scope = vnotch::trim(raw_scope);
            if (scope.empty()) return true;

            if (!vnotch::starts_with_ignore_case(scope, u"AND "))
            {
                if (scope.size() > 1024) return false;
                bool is_file = vnotch::starts_with_ignore_case(scope, u"file:");
                bool is_drive = scope.size() >= 2 && is_ascii_alpha(scope[0]) && scope[1] == u':';
                if (!is_file && !is_drive) return false;

                out_scope.reserve(scope.size() + 24);
                out_scope.append(is_file ? u"AND SCOPE='" : u"AND SCOPE='file:");
                for (char16_t c : scope)
                {
                    if (c == u'\'') out_scope.append(u"''");
                    else if (c == u'\\') out_scope.push_back(u'/');
                    else out_scope.push_back(c);
                }
                out_scope.push_back(u'\'');
                return true;
            }

            if (scope.size() > 4096) return false;

            out_scope.reserve(scope.size() + 16);
            bool in_quote = false;
            int32_t paren_depth = 0;
            char16_t prev_non_space = 0;

            for (size_t i = 0; i < scope.size(); ++i)
            {
                char16_t c = scope[i];
                if (!in_quote)
                {
                    if (c == u' ')
                    {
                        out_scope.push_back(c);
                        continue;
                    }
                    if (c == u'(')
                    {
                        ++paren_depth;
                        prev_non_space = c;
                        out_scope.push_back(c);
                        continue;
                    }
                    if (c == u')')
                    {
                        if (--paren_depth < 0) return false;
                        prev_non_space = c;
                        out_scope.push_back(c);
                        continue;
                    }
                    if (c == u'\'')
                    {
                        if (prev_non_space != u'=' && prev_non_space != u'>' && prev_non_space != u'E' && prev_non_space != u'e')
                            return false;
                        in_quote = true;
                        out_scope.push_back(c);
                        continue;
                    }
                    if (is_ascii_alpha(c) || c == u'.' || c == u'_' || c == u'=' || c == u'<' || c == u'>')
                    {
                        prev_non_space = c;
                        out_scope.push_back(c);
                        continue;
                    }
                    if (is_ascii_digit(c) && (is_ascii_alpha(prev_non_space) || is_ascii_digit(prev_non_space) || prev_non_space == u'_'))
                    {
                        prev_non_space = c;
                        out_scope.push_back(c);
                        continue;
                    }
                    return false;
                }
                else
                {
                    if (c == u'\'')
                    {
                        if (i + 1 < scope.size() && scope[i + 1] == u'\'')
                        {
                            out_scope.append(u"''");
                            ++i;
                            continue;
                        }
                        size_t next = i + 1;
                        while (next < scope.size() && scope[next] == u' ') ++next;
                        std::u16string_view rem = scope.substr(next);
                        bool closes_literal =
                            rem.empty() ||
                            rem[0] == u')' ||
                            vnotch::starts_with_ignore_case(rem, u"OR ") ||
                            vnotch::starts_with_ignore_case(rem, u"OR(") ||
                            vnotch::starts_with_ignore_case(rem, u"AND ") ||
                            vnotch::starts_with_ignore_case(rem, u"AND(");
                        if (closes_literal)
                        {
                            in_quote = false;
                            prev_non_space = u'\'';
                            out_scope.push_back(u'\'');
                        }
                        else
                        {
                            out_scope.append(u"''");
                        }
                    }
                    else
                    {
                        out_scope.push_back(c);
                    }
                }
            }

            return !in_quote && paren_depth == 0;
        }

        static int32_t vn_windows_search_sanitize_query_impl(
            const char16_t* query_ptr, int32_t query_len,
            char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
            if (!out_len || query_len < 0 || (!query_ptr && query_len > 0)) return VN_ERR_INVALID_ARG;
            std::u16string sanitized = sanitize_query_str(vnotch::make_view(query_ptr, query_len));
            return vnotch::write_u16_out(sanitized, out_buf, out_cap, out_len);
        }

        static int32_t vn_windows_search_escape_like_pattern_impl(
            const char16_t* query_ptr, int32_t query_len,
            char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
            if (!out_len || query_len < 0 || (!query_ptr && query_len > 0)) return VN_ERR_INVALID_ARG;
            std::u16string_view view = vnotch::make_view(query_ptr, std::min<int32_t>(query_len, 256));
            std::u16string res = escape_like_pattern_impl(view);
            return vnotch::write_u16_out(res, out_buf, out_cap, out_len);
        }

        static int32_t vn_windows_search_build_name_predicate_impl(
            const char16_t* sanitized_ptr, int32_t sanitized_len,
            char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
            if (!out_len || sanitized_len < 0 || (!sanitized_ptr && sanitized_len > 0)) return VN_ERR_INVALID_ARG;
            std::u16string clean = sanitize_query_str(vnotch::make_view(sanitized_ptr, sanitized_len));
            if (clean.empty())
            {
                *out_len = 0;
                return VN_ERR_INVALID_ARG;
            }
            std::u16string out = build_name_predicate_str(clean);
            return vnotch::write_u16_out(out, out_buf, out_cap, out_len);
        }

        static int32_t vn_windows_search_build_query_sql_impl(
            const char16_t* sanitized_ptr, int32_t sanitized_len,
            const char16_t* original_ptr, int32_t original_len,
            const char16_t* scope_ptr, int32_t scope_len,
            uint8_t is_explicit_path,
            int32_t fetch,
            char16_t* out_buf, int32_t out_cap, int32_t* out_len)
        {
            if (!out_len ||
                sanitized_len < 0 || (!sanitized_ptr && sanitized_len > 0) ||
                original_len < 0 || (!original_ptr && original_len > 0) ||
                scope_len < 0 || (!scope_ptr && scope_len > 0) ||
                fetch <= 0)
            {
                return VN_ERR_INVALID_ARG;
            }

            fetch = std::min<int32_t>(fetch, 500);

            std::u16string predicate;
            if (is_explicit_path != 0)
            {
                std::u16string_view raw_orig = vnotch::make_view(original_ptr, original_len);
                for (char16_t c : raw_orig)
                {
                    if (c < 0x20) return VN_ERR_INVALID_ARG;
                }
                std::u16string_view orig = vnotch::trim(raw_orig);
                if (orig.size() > 256)
                {
                    orig = orig.substr(0, 256);
                    if ((orig.back() & 0xFC00) == 0xD800) orig.remove_suffix(1);
                }
                while (!orig.empty() && orig.front() == u'"') orig.remove_prefix(1);
                while (!orig.empty() && orig.back() == u'"') orig.remove_suffix(1);
                if (orig.empty()) return VN_ERR_INVALID_ARG;

                std::u16string normalized;
                normalized.reserve(orig.size() + 8);
                for (char16_t c : orig)
                {
                    if (c == u'"') continue;
                    if (c == u'/') normalized.push_back(u'\\');
                    else if (c == u'\'') normalized.append(u"''");
                    else normalized.push_back(c);
                }
                if (normalized.empty()) return VN_ERR_INVALID_ARG;

                std::u16string escaped = escape_like_pattern_impl(normalized);
                predicate.reserve(escaped.size() + 36);
                predicate.append(u"System.ItemPathDisplay LIKE '");
                predicate.append(escaped);
                predicate.append(u"%'");
            }
            else
            {
                std::u16string clean = sanitize_query_str(vnotch::make_view(sanitized_ptr, sanitized_len));
                if (clean.empty()) return VN_ERR_INVALID_ARG;
                predicate = build_name_predicate_str(clean);
            }

            std::u16string clean_scope;
            if (!sanitize_scope_clause(vnotch::make_view(scope_ptr, scope_len), clean_scope))
            {
                return VN_ERR_INVALID_ARG;
            }

            char16_t fetch_digits[16];
            int32_t fetch_pos = 16;
            uint32_t ufetch = static_cast<uint32_t>(fetch);
            do
            {
                fetch_digits[--fetch_pos] = static_cast<char16_t>(u'0' + (ufetch % 10));
                ufetch /= 10;
            } while (ufetch > 0);
            std::u16string_view fetch_str(fetch_digits + fetch_pos, static_cast<size_t>(16 - fetch_pos));

            std::u16string sql;
            sql.reserve(160 + predicate.size() + clean_scope.size());
            sql.append(u"SELECT TOP ");
            sql.append(fetch_str);
            sql.append(u" System.ItemNameDisplay, System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:' AND (");
            sql.append(predicate);
            sql.append(u") ");
            sql.append(clean_scope);
            if (!clean_scope.empty()) sql.push_back(u' ');
            sql.append(u"ORDER BY System.Search.Rank DESC");

            return vnotch::write_u16_out(sql, out_buf, out_cap, out_len);
        }

        static int32_t vn_windows_search_normalize_result_path_impl(
            const char16_t* raw_path_ptr,
            int32_t raw_path_len,
            char16_t* out_buf,
            int32_t out_cap,
            int32_t* out_len)
        {
            if (!out_len || raw_path_len < 0 || (!raw_path_ptr && raw_path_len > 0)) return VN_ERR_INVALID_ARG;
            std::u16string_view raw = vnotch::make_view(raw_path_ptr, raw_path_len);
            for (char16_t c : raw)
            {
                if (c == u'\0')
                {
                    *out_len = 0;
                    return VN_OK;
                }
            }

            std::u16string_view s = vnotch::trim(raw);
            if (s.empty())
            {
                *out_len = 0;
                return VN_OK;
            }

            bool is_file_uri = vnotch::starts_with_ignore_case(s, u"file:");
            std::u16string out;
            out.reserve(s.size());

            if (is_file_uri)
            {
                s.remove_prefix(5);
                size_t slashes = 0;
                while (slashes < s.size() && (s[slashes] == u'/' || s[slashes] == u'\\'))
                {
                    ++slashes;
                }

                if (slashes + 2 <= s.size() &&
                    is_ascii_alpha(s[slashes]) &&
                    (s[slashes + 1] == u':' || s[slashes + 1] == u'|'))
                {
                    s.remove_prefix(slashes);
                }
                else if (slashes >= 2)
                {
                    out.append(u"\\\\");
                    s.remove_prefix(slashes);
                }

                std::string utf8_bytes;
                for (size_t i = 0; i < s.size(); ++i)
                {
                    char16_t c = s[i];
                    if (c == u'%' && i + 2 < s.size() && hex_val(s[i + 1]) >= 0 && hex_val(s[i + 2]) >= 0)
                    {
                        utf8_bytes.clear();
                        while (i + 2 < s.size() && s[i] == u'%')
                        {
                            int hi = hex_val(s[i + 1]);
                            int lo = hex_val(s[i + 2]);
                            if (hi < 0 || lo < 0) break;
                            char byte_val = static_cast<char>((hi << 4) | lo);
                            if (byte_val == '\0')
                            {
                                *out_len = 0;
                                return VN_OK;
                            }
                            utf8_bytes.push_back(byte_val);
                            i += 3;
                        }
                        --i;

                        int wlen = ::MultiByteToWideChar(
                            CP_UTF8, 0,
                            utf8_bytes.data(), static_cast<int>(utf8_bytes.size()),
                            nullptr, 0);
                        if (wlen > 0)
                        {
                            size_t old_size = out.size();
                            out.resize(old_size + static_cast<size_t>(wlen));
                            ::MultiByteToWideChar(
                                CP_UTF8, 0,
                                utf8_bytes.data(), static_cast<int>(utf8_bytes.size()),
                                reinterpret_cast<wchar_t*>(out.data() + old_size), wlen);
                            for (size_t k = old_size; k < out.size(); ++k)
                            {
                                if (out[k] == u'/') out[k] = u'\\';
                            }
                        }
                        continue;
                    }
                    if (c == u'/') c = u'\\';
                    out.push_back(c);
                }

                if (out.size() >= 2 && is_ascii_alpha(out[0]) && out[1] == u'|')
                {
                    out[1] = u':';
                }
            }
            else
            {
                for (char16_t c : s)
                {
                    out.push_back(c == u'/' ? u'\\' : c);
                }
            }

            return vnotch::write_u16_out(out, out_buf, out_cap, out_len);
        }
    };
} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_windows_search_sanitize_query(
    const char16_t* query_ptr, int32_t query_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::WindowsSearchProvider::vn_windows_search_sanitize_query_impl(query_ptr, query_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_windows_search_escape_like_pattern(
    const char16_t* query_ptr, int32_t query_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::WindowsSearchProvider::vn_windows_search_escape_like_pattern_impl(query_ptr, query_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_windows_search_build_name_predicate(
    const char16_t* sanitized_ptr, int32_t sanitized_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::WindowsSearchProvider::vn_windows_search_build_name_predicate_impl(sanitized_ptr, sanitized_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_windows_search_build_query_sql(
    const char16_t* sanitized_ptr, int32_t sanitized_len,
    const char16_t* original_ptr, int32_t original_len,
    const char16_t* scope_ptr, int32_t scope_len,
    uint8_t is_explicit_path,
    int32_t fetch,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try
    {
        return vnotch::services::WindowsSearchProvider::vn_windows_search_build_query_sql_impl(
            sanitized_ptr, sanitized_len,
            original_ptr, original_len,
            scope_ptr, scope_len,
            is_explicit_path,
            fetch,
            out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_windows_search_normalize_result_path(
    const char16_t* raw_path_ptr,
    int32_t raw_path_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    try
    {
        return vnotch::services::WindowsSearchProvider::vn_windows_search_normalize_result_path_impl(
            raw_path_ptr, raw_path_len, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
