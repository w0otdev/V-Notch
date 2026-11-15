#include "vnotch_ext_core.h"

#include <tlhelp32.h>
#include <devpropdef.h>
#include <cfgmgr32.h>
#include <cwchar>

#pragma comment(lib, "cfgmgr32.lib")

#include "Parsing/WindowsText.h"
using namespace vnotch::windows_text;

VN_EXPORT int32_t __stdcall vn_settings_is_generated_backup_name(
    const wchar_t* name,
    int32_t len,
    uint8_t* out_valid) noexcept
{
    if (!out_valid || len < 0 || (len > 0 && !name))
        return VN_ERR_INVALID_ARG;

    *out_valid = 0;
    static constexpr std::wstring_view kPrefix = L"settings.bak-";
    static constexpr std::wstring_view kSuffix = L".json";

    if (len != 37 && len != 70)
        return VN_OK;

    const std::wstring_view full(name, static_cast<size_t>(len));
    if (!full.starts_with(kPrefix) || !full.ends_with(kSuffix))
        return VN_OK;

    std::wstring_view stamp = full.substr(kPrefix.size(), full.size() - kPrefix.size() - kSuffix.size());
    if (stamp.size() == 52)
    {
        if (stamp[19] != L'-')
            return VN_OK;
        for (size_t i = 20; i < 52; ++i)
        {
            if (!is_ascii_hex(stamp[i]))
                return VN_OK;
        }
        stamp = stamp.substr(0, 19);
    }

    if (stamp.size() != 19 || stamp[8] != L'-' || stamp[15] != L'-')
        return VN_OK;

    const int32_t year = parse_digits(stamp.substr(0, 4));
    const int32_t month = parse_digits(stamp.substr(4, 2));
    const int32_t day = parse_digits(stamp.substr(6, 2));
    const int32_t hour = parse_digits(stamp.substr(9, 2));
    const int32_t minute = parse_digits(stamp.substr(11, 2));
    const int32_t second = parse_digits(stamp.substr(13, 2));
    const int32_t ms = parse_digits(stamp.substr(16, 3));

    if (year < 1 || year > 9999 || month < 1 || month > 12)
        return VN_OK;

    const int32_t max_day = days_in_month(year, month);
    if (day < 1 || day > max_day)
        return VN_OK;
    if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59 || ms < 0 || ms > 999)
        return VN_OK;

    *out_valid = 1;
    return VN_OK;
}

