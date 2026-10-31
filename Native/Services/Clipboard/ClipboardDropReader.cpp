#include "vnotch_common.h"

namespace vnotch::services
{

class ClipboardDropReader final
{
public:
    static bool ExtractDownloadUrlImage(std::u16string_view download, std::u16string_view& out_url) noexcept
    {
        out_url = {};
        if (!vnotch::starts_with_ignore_case(download, u"image/"))
        {
            return false;
        }
        size_t first = download.find(u':');
        if (first == std::u16string_view::npos)
        {
            return false;
        }
        size_t second = download.find(u':', first + 1);
        if (second == std::u16string_view::npos || second <= first)
        {
            return false;
        }
        out_url = download.substr(second + 1);
        return true;
    }

    static bool ValidateDataUriImage(
        std::u16string_view value,
        int32_t max_image_bytes,
        int32_t& out_comma_index) noexcept
    {
        out_comma_index = -1;
        size_t comma = value.find(u',');
        if (comma == std::u16string_view::npos)
        {
            return false;
        }
        std::u16string_view header = value.substr(0, comma);
        if (!vnotch::starts_with_ignore_case(header, u"data:image/") ||
            !vnotch::ends_with_ignore_case(header, u";base64"))
        {
            return false;
        }
        int64_t max_b64_len = (static_cast<int64_t>(max_image_bytes) * 4LL) / 3LL + 4LL;
        int64_t actual_b64_len = static_cast<int64_t>(value.size()) - static_cast<int64_t>(comma);
        if (actual_b64_len > max_b64_len)
        {
            return false;
        }
        out_comma_index = static_cast<int32_t>(comma);
        return true;
    }

    static std::u16string StripHtmlFragmentTags(std::u16string_view html)
    {
        constexpr std::u16string_view kStartMarker = u"<!--StartFragment-->";
        constexpr std::u16string_view kEndMarker = u"<!--EndFragment-->";

        int32_t start = vnotch::index_of_ordinal(html, kStartMarker);
        int32_t end = vnotch::index_of_ordinal(html, kEndMarker);
        std::u16string_view fragment = (start >= 0 && end > start)
            ? html.substr(static_cast<size_t>(start) + kStartMarker.size(),
                          static_cast<size_t>(end - start) - kStartMarker.size())
            : html;

        std::u16string out;
        out.reserve(fragment.size());
        size_t i = 0;
        while (i < fragment.size())
        {
            if (fragment[i] == u'<')
            {
                size_t close = fragment.find(u'>', i + 1);
                if (close != std::u16string_view::npos && close > i + 1)
                {
                    out.push_back(u' ');
                    i = close + 1;
                    continue;
                }
            }
            out.push_back(fragment[i]);
            ++i;
        }
        return out;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_clipboard_drop_extract_download_url_image(
    const char16_t* download_ptr,
    int32_t download_len,
    uint8_t* out_found,
    int32_t* out_start_index) noexcept
{
    if (out_found == nullptr || out_start_index == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_found = 0u;
    *out_start_index = 0;
    std::u16string_view view = vnotch::make_u16_view(download_ptr, download_len);
    std::u16string_view url;
    if (vnotch::services::ClipboardDropReader::ExtractDownloadUrlImage(view, url))
    {
        *out_found = 1u;
        *out_start_index = static_cast<int32_t>(url.data() - view.data());
    }
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_drop_validate_data_uri_image(
    const char16_t* uri_ptr,
    int32_t uri_len,
    int32_t max_image_bytes,
    uint8_t* out_valid,
    int32_t* out_comma_index) noexcept
{
    if (out_valid == nullptr || out_comma_index == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    int32_t comma = -1;
    bool ok = uri_ptr != nullptr &&
              vnotch::services::ClipboardDropReader::ValidateDataUriImage(
                  vnotch::make_u16_view(uri_ptr, uri_len), max_image_bytes, comma);
    *out_valid = ok ? 1u : 0u;
    *out_comma_index = comma;
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_drop_strip_html_fragment_tags(
    const char16_t* html_ptr,
    int32_t html_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    try
    {
        std::u16string stripped = vnotch::services::ClipboardDropReader::StripHtmlFragmentTags(
            vnotch::make_u16_view(html_ptr, html_len));
        return vnotch::copy_out_u16(stripped, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
