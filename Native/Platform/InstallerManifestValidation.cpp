#include "vnotch_ext_ui.h"

#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>

#include "Controls/UiValueMath.h"
using namespace vnotch::ui;

VN_EXPORT int32_t __stdcall vn_install_cleanup_validate_manifest_entry(
    const wchar_t* rel_path,
    int32_t path_len,
    const wchar_t* sha256,
    int32_t sha_len,
    uint8_t* out_valid) noexcept
{
    if (!out_valid)
        return VN_ERR_INVALID_ARG;

    *out_valid = 0;

    if (!rel_path || path_len <= 0 || !sha256 || sha_len != 64)
        return VN_OK;

    const std::u16string_view path_sv(reinterpret_cast<const char16_t*>(rel_path), static_cast<size_t>(path_len));
    const std::u16string_view sha_sv(reinterpret_cast<const char16_t*>(sha256), 64);

    for (char16_t ch : sha_sv)
    {
        if (!IsAsciiHexDigit(ch))
            return VN_OK;
    }

    bool any_non_ws = false;
    for (char16_t ch : path_sv)
    {
        if (ch == 0)
            return VN_OK;
        if (!IsUtf16WhiteSpace(ch))
            any_non_ws = true;
    }
    if (!any_non_ws)
        return VN_OK;

    // Path.IsPathRooted on Windows: leading slash/backslash or drive-letter colon
    if (path_sv[0] == u'/' || path_sv[0] == u'\\')
        return VN_OK;
    if (path_sv.size() >= 2 && IsAsciiAlpha(path_sv[0]) && path_sv[1] == u':')
        return VN_OK;

    //Equals("installed-files.json", OrdinalIgnoreCase)
    if (EqualsAsciiIgnoreCase(path_sv, u"installed-files.json"))
        return VN_OK;

    // Split(['/', '\\']).Any(part => part is "" or "." or ".." || part.Contains(':'))
    size_t seg_start = 0;
    for (size_t i = 0; i <= path_sv.size(); ++i)
    {
        const bool at_sep = (i == path_sv.size()) || path_sv[i] == u'/' || path_sv[i] == u'\\';
        if (!at_sep)
        {
            if (path_sv[i] == u':')
                return VN_OK;
            continue;
        }

        const size_t seg_len = i - seg_start;
        if (seg_len == 0)
            return VN_OK;
        if (seg_len == 1 && path_sv[seg_start] == u'.')
            return VN_OK;
        if (seg_len == 2 && path_sv[seg_start] == u'.' && path_sv[seg_start + 1] == u'.')
            return VN_OK;

        seg_start = i + 1;
    }

    *out_valid = 1;
    return VN_OK;
}
