#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_soundcloud_extract_anchor_hrefs(
    const wchar_t* html,
    int32_t html_len,
    VnCoreTextSlice* out_slices,
    int32_t slice_cap,
    int32_t* out_total_count) noexcept
{
    if (!out_total_count || html_len < 0 || slice_cap < 0 ||
        (html_len > 0 && !html) || (slice_cap > 0 && !out_slices))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_total_count = 0;
    if (html_len < 4)
        return VN_OK;

    int32_t total = 0;
    int32_t i = 0;
    const std::wstring_view markup(html, static_cast<size_t>(html_len));
    while (i + 3 < html_len)
    {
        if (markup.substr(static_cast<size_t>(i), 4) == L"<!--")
        {
            const auto end = markup.find(L"-->", static_cast<size_t>(i) + 4);
            if (end == std::wstring_view::npos) break;
            i = static_cast<int32_t>(end + 3);
            continue;
        }

        if (html[i] != L'<' || to_ascii_lower_w(html[i + 1]) != L'a' || !is_html_space(html[i + 2]))
        {
            ++i;
            continue;
        }

        i += 3;
        bool found_href_in_tag = false;
        while (i < html_len && html[i] != L'>')
        {
            while (i < html_len && (is_html_space(html[i]) || html[i] == L'/'))
            {
                if (html[i] == L'>') break;
                ++i;
            }
            if (i >= html_len || html[i] == L'>')
                break;

            const int32_t attr_start = i;
            while (i < html_len && !is_html_space(html[i]) &&
                   html[i] != L'=' && html[i] != L'>' && html[i] != L'"' && html[i] != L'\'')
            {
                ++i;
            }
            const int32_t attr_len = i - attr_start;

            while (i < html_len && is_html_space(html[i]))
                ++i;

            if (i < html_len && html[i] == L'=')
            {
                ++i;
                while (i < html_len && is_html_space(html[i]))
                    ++i;

                int32_t val_start = i;
                int32_t val_len = 0;
                if (i < html_len && (html[i] == L'"' || html[i] == L'\''))
                {
                    const wchar_t quote = html[i++];
                    val_start = i;
                    while (i < html_len && html[i] != quote)
                        ++i;
                    val_len = i - val_start;
                    if (i < html_len && html[i] == quote)
                        ++i;
                }
                else
                {
                    val_start = i;
                    while (i < html_len && !is_html_space(html[i]) && html[i] != L'>')
                        ++i;
                    val_len = i - val_start;
                }

                if (!found_href_in_tag &&
                    attr_len == 4 &&
                    equals_ascii_icase_w(std::wstring_view(html + attr_start, 4), L"href"))
                {
                    found_href_in_tag = true;
                    if (val_len > 0)
                    {
                        if (total < slice_cap && out_slices)
                        {
                            out_slices[total].offset = val_start;
                            out_slices[total].length = val_len;
                        }
                        ++total;
                    }
                }
            }
            else if (attr_len == 0 && i < html_len && html[i] != L'>')
            {
                // Skip stray quote or delimiter inside malformed markup
                ++i;
            }
        }

        if (i < html_len && html[i] == L'>')
            ++i;
    }

    *out_total_count = total;
    return (total > slice_cap) ? VN_ERR_BUFFER_TOO_SMALL : VN_OK;
}

