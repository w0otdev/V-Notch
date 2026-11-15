#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_file_icon_can_probe_local_path(
    const wchar_t* path,
    int32_t len,
    uint8_t* out_can_probe) noexcept
{
    if (!out_can_probe || len < 0 || (len > 0 && !path))
        return VN_ERR_INVALID_ARG;

    *out_can_probe = 0;
    if (len == 0)
        return VN_OK;

    int32_t start = 0;
    int32_t end = len;
    while (start < end && is_dotnet_whitespace(path[start]))
        ++start;
    while (end > start && is_dotnet_whitespace(path[end - 1]))
        --end;

    const int32_t trimmed_len = end - start;
    if (trimmed_len < 3)
        return VN_OK;

    for (int32_t i = start; i < end; ++i)
    {
        if (path[i] == L'\0')
            return VN_OK;
    }

    if (!is_ascii_letter(path[start]) || path[start + 1] != L':' ||
        (path[start + 2] != L'\\' && path[start + 2] != L'/'))
    {
        return VN_OK;
    }

    *out_can_probe = 1;
    return VN_OK;
}

