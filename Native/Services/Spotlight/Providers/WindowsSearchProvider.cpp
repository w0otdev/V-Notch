#include "vnotch_common.h"
#include <algorithm>
#include <string>

using namespace vnotch;

namespace vnotch::services
{
    class WindowsSearchProvider final
    {
    public:
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

        static int32_t vn_windows_search_sanitize_query_impl(
            const char16_t* query_ptr, int32_t query_len,
            char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
            if (!out_len) return VN_ERR_INVALID_ARG;
            std::u16string_view q = vnotch::make_view(query_ptr, query_len);

            std::u16string filtered;
            filtered.reserve(std::min<size_t>(q.size(), 256));
            for (char16_t c : q)
            {
                if (c == u'"' || c == u'\'' || c == u'\\' || c == u'(' || c == u')' || c == u':' || c == u'*')
                    continue;
                filtered.push_back(c);
                if (filtered.size() == 256)
                {
                    if ((filtered.back() & 0xFC00) == 0xD800) filtered.pop_back();
                    break;
                }
            }

            std::u16string_view trimmed = vnotch::trim(filtered);
            return vnotch::write_u16_out(trimmed, out_buf, out_cap, out_len);
        }

        static int32_t vn_windows_search_escape_like_pattern_impl(
            const char16_t* query_ptr, int32_t query_len,
            char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
            if (!out_len) return VN_ERR_INVALID_ARG;
            std::u16string res = escape_like_pattern_impl(vnotch::make_view(query_ptr, query_len));
            return vnotch::write_u16_out(res, out_buf, out_cap, out_len);
        }

        static int32_t vn_windows_search_build_name_predicate_impl(
            const char16_t* sanitized_ptr, int32_t sanitized_len,
            char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
            if (!out_len) return VN_ERR_INVALID_ARG;
            std::u16string out = build_name_predicate_str(vnotch::make_view(sanitized_ptr, sanitized_len));
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
            if (!out_len) return VN_ERR_INVALID_ARG;

            std::u16string predicate;
            if (is_explicit_path != 0)
            {
                std::u16string_view orig = vnotch::trim(vnotch::make_view(original_ptr, original_len));
                while (!orig.empty() && orig.front() == u'"') orig.remove_prefix(1);
                while (!orig.empty() && orig.back() == u'"') orig.remove_suffix(1);

                std::u16string normalized;
                normalized.reserve(orig.size() + 8);
                for (char16_t c : orig)
                {
                    if (c == u'/') normalized.push_back(u'\\');
                    else if (c == u'\'') normalized.append(u"''");
                    else normalized.push_back(c);
                }

                std::u16string escaped = escape_like_pattern_impl(normalized);
                predicate.reserve(escaped.size() + 36);
                predicate.append(u"System.ItemPathDisplay LIKE '");
                predicate.append(escaped);
                predicate.append(u"%'");
            }
            else
            {
                predicate = build_name_predicate_str(vnotch::make_view(sanitized_ptr, sanitized_len));
            }

            std::u16string_view scope = vnotch::make_view(scope_ptr, scope_len);

            char16_t fetch_digits[16];
            int32_t fetch_pos = 16;
            uint32_t ufetch = fetch < 0 ? static_cast<uint32_t>(-static_cast<int64_t>(fetch)) : static_cast<uint32_t>(fetch);
            do
            {
                fetch_digits[--fetch_pos] = static_cast<char16_t>(u'0' + (ufetch % 10));
                ufetch /= 10;
            } while (ufetch > 0);
            if (fetch < 0) fetch_digits[--fetch_pos] = u'-';
            std::u16string_view fetch_str(fetch_digits + fetch_pos, static_cast<size_t>(16 - fetch_pos));

            std::u16string sql;
            sql.reserve(160 + predicate.size() + scope.size());
            sql.append(u"SELECT TOP ");
            sql.append(fetch_str);
            sql.append(u" System.ItemNameDisplay, System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:' AND (");
            sql.append(predicate);
            sql.append(u") ");
            sql.append(scope);
            sql.append(u" ORDER BY System.Search.Rank DESC");

            return vnotch::write_u16_out(sql, out_buf, out_cap, out_len);
        }

        static constexpr int hex_val(char16_t c) noexcept
        {
            if (c >= u'0' && c <= u'9') return c - u'0';
            if (c >= u'a' && c <= u'f') return 10 + (c - u'a');
            if (c >= u'A' && c <= u'F') return 10 + (c - u'A');
            return -1;
        }

        static constexpr bool is_ascii_alpha(char16_t c) noexcept
        {
            return (c >= u'a' && c <= u'z') || (c >= u'A' && c <= u'Z');
        }

        static int32_t vn_windows_search_normalize_result_path_impl(
            const char16_t* raw_path_ptr,
            int32_t raw_path_len,
            char16_t* out_buf,
            int32_t out_cap,
            int32_t* out_len)
        {
            if (!out_len) return VN_ERR_INVALID_ARG;
            std::u16string_view s = vnotch::trim(vnotch::make_view(raw_path_ptr, raw_path_len));
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

                for (size_t i = 0; i < s.size(); ++i)
                {
                    char16_t c = s[i];
                    if (c == u'%' && i + 2 < s.size())
                    {
                        int hi = hex_val(s[i + 1]);
                        int lo = hex_val(s[i + 2]);
                        if (hi >= 0 && lo >= 0)
                        {
                            c = static_cast<char16_t>((hi << 4) | lo);
                            i += 2;
                        }
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
