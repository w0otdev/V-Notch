#include "vnotch_common.h"

namespace vnotch::services
{

class SpotifyQueryMetadataProvider final
{
    static constexpr bool IsAsciiHex(char16_t c) noexcept
    {
        return (c >= u'0' && c <= u'9') ||
               (c >= u'a' && c <= u'f') ||
               (c >= u'A' && c <= u'F');
    }

    static bool MatchScriptPrefix(
        std::u16string_view html,
        std::u16string_view prefix,
        int32_t& out_start,
        int32_t& out_len) noexcept
    {
        size_t search_from = 0;
        while (search_from < html.size())
        {
            int32_t rel = vnotch::index_of_ignore_case(html.substr(search_from), prefix);
            if (rel < 0)
            {
                return false;
            }
            size_t start = search_from + static_cast<size_t>(rel);
            size_t pos = start + prefix.size();
            size_t hex_start = pos;
            while (pos < html.size() && IsAsciiHex(html[pos]))
            {
                ++pos;
            }
            if (pos > hex_start && pos < html.size() && html[pos] == u'.')
            {
                std::u16string_view rem = html.substr(pos);
                if (vnotch::starts_with_ignore_case(rem, u".mjs"))
                {
                    out_start = static_cast<int32_t>(start);
                    out_len = static_cast<int32_t>((pos + 4) - start);
                    return true;
                }
                if (vnotch::starts_with_ignore_case(rem, u".js"))
                {
                    out_start = static_cast<int32_t>(start);
                    out_len = static_cast<int32_t>((pos + 3) - start);
                    return true;
                }
            }
            search_from = start + 1;
        }
        return false;
    }

public:
    static bool FindScriptUrl(
        std::u16string_view html,
        uint8_t prefer_mobile,
        int32_t& out_start,
        int32_t& out_len) noexcept
    {
        constexpr std::u16string_view kMobilePrefix =
            u"https://open.spotifycdn.com/cdn/build/mobile-web-player/mobile-web-player.";
        constexpr std::u16string_view kDesktopPrefix =
            u"https://open.spotifycdn.com/cdn/build/web-player/web-player.";

        std::u16string_view first = prefer_mobile != 0 ? kMobilePrefix : kDesktopPrefix;
        std::u16string_view second = prefer_mobile != 0 ? kDesktopPrefix : kMobilePrefix;

        if (MatchScriptPrefix(html, first, out_start, out_len))
        {
            return true;
        }
        return MatchScriptPrefix(html, second, out_start, out_len);
    }

    static bool ContainsPersistedQueryNotFound(std::u16string_view json) noexcept
    {
        return vnotch::contains_ignore_case(json, u"PersistedQueryNotFound");
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_spotify_query_meta_find_script_url(
    const char16_t* html_ptr,
    int32_t html_len,
    uint8_t prefer_mobile,
    uint8_t* out_found,
    int32_t* out_start,
    int32_t* out_len) noexcept
{
    if (out_found == nullptr || out_start == nullptr || out_len == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    int32_t s = 0, l = 0;
    bool ok = html_ptr != nullptr &&
              vnotch::services::SpotifyQueryMetadataProvider::FindScriptUrl(
                  vnotch::make_u16_view(html_ptr, html_len), prefer_mobile, s, l);
    *out_found = ok ? 1u : 0u;
    *out_start = s;
    *out_len = l;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_query_meta_contains_not_found(
    const char16_t* json_ptr,
    int32_t json_len,
    uint8_t* out_contains) noexcept
{
    if (out_contains == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_contains = (json_ptr != nullptr &&
                     vnotch::services::SpotifyQueryMetadataProvider::ContainsPersistedQueryNotFound(
                         vnotch::make_u16_view(json_ptr, json_len))) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
