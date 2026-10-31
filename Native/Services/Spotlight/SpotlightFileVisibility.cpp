#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class SpotlightFileVisibility final
    {
    public:
        static constexpr std::u16string_view kHiddenDirectories[] = {
                u"AppData", u"$Recycle.Bin", u"System Volume Information", u".git", u".svn", u".hg",
                u".vs", u".vscode", u".idea", u".cache", u".nuget", u"node_modules", u"__pycache__", u"obj",
                u"Cache", u"Caches", u"Temp"
            };
        
            static constexpr std::u16string_view kHiddenExtensions[] = {
                u".dll", u".sys", u".drv", u".ocx", u".pdb", u".obj", u".mui", u".cat",
                u".tmp", u".temp", u".cache", u".etl", u".manifest"
            };
        
            static std::u16string_view trim_quotes(std::u16string_view sv) noexcept
            {
                sv = vnotch::trim(sv);
                while (!sv.empty() && sv.front() == u'"') sv.remove_prefix(1);
                while (!sv.empty() && sv.back() == u'"') sv.remove_suffix(1);
                return sv;
            }
        
            static bool is_path_fully_qualified(std::u16string_view path) noexcept
            {
                if (path.size() >= 3 && vnotch::is_ascii_alpha(path[0]) && path[1] == u':' &&
                    (path[2] == u'\\' || path[2] == u'/'))
                {
                    return true;
                }
                if (path.size() >= 2 && (path[0] == u'\\' || path[0] == u'/') &&
                    (path[1] == u'\\' || path[1] == u'/'))
                {
                    return true;
                }
                return false;
            }
        
            static bool is_explicit_path_impl(std::u16string_view query) noexcept
            {
                return is_path_fully_qualified(trim_quotes(query));
            }
        
            static std::u16string_view get_file_name(std::u16string_view path) noexcept
            {
                return vnotch::get_file_name(path);
            }
        
            static std::u16string_view get_extension(std::u16string_view path) noexcept
            {
                return vnotch::get_extension(path);
            }
        
            static std::u16string_view get_file_name_without_extension(std::u16string_view path) noexcept
            {
                return vnotch::get_stem(path);
            }
        
            static bool is_file_name_query_impl(std::u16string_view query) noexcept
            {
                std::u16string_view name = trim_quotes(query);
                if (name.size() <= 2) return false;
                for (char16_t c : name)
                {
                    if (c == u'\\' || c == u'/' || c == u'*' || c == u'?' || c == u':' ||
                        c == u'<' || c == u'>' || c == u'|')
                    {
                        return false;
                    }
                }
                return !get_file_name_without_extension(name).empty() && get_extension(name).size() > 1;
            }
        
            static bool is_unc_path_impl(std::u16string_view path) noexcept
            {
                std::u16string_view s = vnotch::trim(path);
                if (s.size() < 2) return false;
                if (vnotch::starts_with_icase(s, u"file://") || vnotch::starts_with_icase(s, u"file:\\\\"))
                {
                    std::u16string_view rest = s.substr(7);
                    if (rest.empty() || rest[0] == u'/' || rest[0] == u'\\') return false;
                    if (vnotch::starts_with_icase(rest, u"localhost/") || vnotch::starts_with_icase(rest, u"localhost\\")) return false;
                    if (rest.size() >= 2 && vnotch::is_ascii_alpha(rest[0]) && (rest[1] == u':' || rest[1] == u'|')) return false;
                    return true;
                }
                bool starts_bs = s[0] == u'\\' && s[1] == u'\\';
                bool starts_fs = s[0] == u'/' && s[1] == u'/';
                if (!starts_bs && !starts_fs) return false;
                if (s.size() >= 4 && s[0] == u'\\' && s[1] == u'\\' && (s[2] == u'?' || s[2] == u'.') && s[3] == u'\\')
                {
                    std::u16string_view rest = s.substr(4);
                    return vnotch::starts_with_icase(rest, u"UNC\\") || vnotch::starts_with_icase(rest, u"UNC/");
                }
                return true;
            }

        static int32_t vn_spotlight_visibility_is_explicit_path_impl(const char16_t* query_ptr, int32_t query_len,
    uint8_t* out_result)
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    *out_result = is_explicit_path_impl(vnotch::make_view(query_ptr, query_len)) ? 1 : 0;
    return VN_OK;
}

        static int32_t vn_spotlight_visibility_should_search_impl(const char16_t* query_ptr, int32_t query_len,
    uint8_t* out_result)
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    std::u16string_view q = vnotch::make_view(query_ptr, query_len);
    *out_result = (vnotch::trim(q).size() >= 2 || is_explicit_path_impl(q)) ? 1 : 0;
    return VN_OK;
}

        static int32_t vn_spotlight_visibility_is_file_name_query_impl(const char16_t* query_ptr, int32_t query_len,
    uint8_t* out_result)
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    *out_result = is_file_name_query_impl(vnotch::make_view(query_ptr, query_len)) ? 1 : 0;
    return VN_OK;
}

        static int32_t vn_spotlight_visibility_should_include_impl(const char16_t* target_ptr, int32_t target_len,
    const char16_t* query_ptr, int32_t query_len,
    uint8_t is_folder,
    uint8_t is_under_hidden_root,
    uint8_t* out_result)
        {
    if (!out_result) return VN_ERR_INVALID_ARG;
    *out_result = 0;

    std::u16string_view target = vnotch::make_view(target_ptr, target_len);
    std::u16string_view query = vnotch::make_view(query_ptr, query_len);

    if (is_unc_path_impl(target)) return VN_OK;

    std::u16string path(target);
    for (char16_t& c : path)
    {
        if (c == u'/') c = u'\\';
    }

    if (is_explicit_path_impl(query))
    {
        std::u16string norm_q(trim_quotes(query));
        for (char16_t& c : norm_q)
        {
            if (c == u'/') c = u'\\';
        }
        *out_result = vnotch::starts_with_icase(path, norm_q) ? 1 : 0;
        return VN_OK;
    }

    if (is_file_name_query_impl(query) && vnotch::equals_icase(get_file_name(path), trim_quotes(query)))
    {
        *out_result = 1;
        return VN_OK;
    }

    if (is_under_hidden_root != 0)
        return VN_OK;

    size_t i = 0;
    while (i < path.size())
    {
        while (i < path.size() && path[i] == u'\\') ++i;
        if (i >= path.size()) break;
        size_t start = i;
        while (i < path.size() && path[i] != u'\\') ++i;
        std::u16string_view seg(path.data() + start, i - start);
        for (std::u16string_view hidden_dir : kHiddenDirectories)
        {
            if (vnotch::equals_icase(seg, hidden_dir))
                return VN_OK;
        }
    }

    if (is_folder != 0)
    {
        *out_result = 1;
        return VN_OK;
    }

    std::u16string_view ext = get_extension(path);
    for (std::u16string_view hidden_ext : kHiddenExtensions)
    {
        if (vnotch::equals_icase(ext, hidden_ext))
            return VN_OK;
    }

    *out_result = 1;
    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_spotlight_visibility_is_explicit_path(const char16_t* query_ptr, int32_t query_len,
    uint8_t* out_result) noexcept
{
    try { return vnotch::services::SpotlightFileVisibility::vn_spotlight_visibility_is_explicit_path_impl(query_ptr, query_len, out_result);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_spotlight_visibility_should_search(const char16_t* query_ptr, int32_t query_len,
    uint8_t* out_result) noexcept
{
    try { return vnotch::services::SpotlightFileVisibility::vn_spotlight_visibility_should_search_impl(query_ptr, query_len, out_result);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_spotlight_visibility_is_file_name_query(const char16_t* query_ptr, int32_t query_len,
    uint8_t* out_result) noexcept
{
    try { return vnotch::services::SpotlightFileVisibility::vn_spotlight_visibility_is_file_name_query_impl(query_ptr, query_len, out_result);
 } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_spotlight_visibility_should_include(const char16_t* target_ptr, int32_t target_len,
    const char16_t* query_ptr, int32_t query_len,
    uint8_t is_folder,
    uint8_t is_under_hidden_root,
    uint8_t* out_result) noexcept
{
    try { return vnotch::services::SpotlightFileVisibility::vn_spotlight_visibility_should_include_impl(target_ptr, target_len, query_ptr, query_len, is_folder, is_under_hidden_root, out_result);
 } catch (...) { return VN_ERR_FAIL; }
}

