#include "vnotch_common.h"

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

        static int32_t vn_windows_search_sanitize_query_impl(const char16_t* query_ptr, int32_t query_len,
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

        static int32_t vn_windows_search_escape_like_pattern_impl(const char16_t* query_ptr, int32_t query_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    std::u16string res = escape_like_pattern_impl(vnotch::make_view(query_ptr, query_len));
    return vnotch::write_u16_out(res, out_buf, out_cap, out_len);
}

        static int32_t vn_windows_search_build_name_predicate_impl(const char16_t* sanitized_ptr, int32_t sanitized_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
    if (!out_len) return VN_ERR_INVALID_ARG;
    std::u16string_view sanitized = vnotch::make_view(sanitized_ptr, sanitized_len);
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

    return vnotch::write_u16_out(out, out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_windows_search_sanitize_query(const char16_t* query_ptr, int32_t query_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::WindowsSearchProvider::vn_windows_search_sanitize_query_impl(query_ptr, query_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_windows_search_escape_like_pattern(const char16_t* query_ptr, int32_t query_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::WindowsSearchProvider::vn_windows_search_escape_like_pattern_impl(query_ptr, query_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_windows_search_build_name_predicate(const char16_t* sanitized_ptr, int32_t sanitized_len,
    char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    try { return vnotch::services::WindowsSearchProvider::vn_windows_search_build_name_predicate_impl(sanitized_ptr, sanitized_len, out_buf, out_cap, out_len); } catch (...) { return VN_ERR_FAIL; }
}

