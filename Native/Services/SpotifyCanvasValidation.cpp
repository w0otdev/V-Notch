#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_spotify_canvas_may_be_mp4_utf8(
    const uint8_t* utf8,
    int32_t len,
    uint8_t* out_maybe) noexcept
{
    if (!out_maybe || len < 0 || (len > 0 && !utf8))
        return VN_ERR_INVALID_ARG;

    *out_maybe = 0;
    // Minimum valid URL is "https://scdn.co/.mp4" (20 bytes)
    if (len < 20)
        return VN_OK;

    int32_t start = 0;
    while (start < len && utf8[start] <= 0x20)
        ++start;

    if (len - start < 20)
        return VN_OK;

    // Must begin with ASCII "https:" (case-insensitive) if ASCII-prefixed
    if (utf8[start] < 0x80)
    {
        static constexpr uint8_t kPrefix[6] = { 'h', 't', 't', 'p', 's', ':' };
        for (int32_t i = 0; i < 6; ++i)
        {
            if (to_ascii_lower_b(utf8[start + i]) != kPrefix[i])
                return VN_OK;
        }
    }

    *out_maybe = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotify_canvas_is_trusted_uri_parts(
    const wchar_t* scheme,
    int32_t scheme_len,
    const wchar_t* host,
    int32_t host_len,
    const wchar_t* abs_path,
    int32_t abs_path_len,
    uint8_t* out_trusted) noexcept
{
    if (!out_trusted || scheme_len < 0 || host_len < 0 || abs_path_len < 0 ||
        (scheme_len > 0 && !scheme) || (host_len > 0 && !host) || (abs_path_len > 0 && !abs_path))
    {
        return VN_ERR_INVALID_ARG;
    }

    *out_trusted = 0;
    const std::wstring_view scheme_sv(scheme ? scheme : L"", static_cast<size_t>(scheme_len));
    const std::wstring_view host_sv(host ? host : L"", static_cast<size_t>(host_len));
    const std::wstring_view path_sv(abs_path ? abs_path : L"", static_cast<size_t>(abs_path_len));

    if (!equals_ascii_icase_w(scheme_sv, L"https"))
        return VN_OK;

    const bool trusted_host = equals_ascii_icase_w(host_sv, L"scdn.co") ||
                              ends_with_ascii_icase_w(host_sv, L".scdn.co");
    if (!trusted_host)
        return VN_OK;

    if (!ends_with_ascii_icase_w(path_sv, L".mp4"))
        return VN_OK;

    *out_trusted = 1;
    return VN_OK;
}

