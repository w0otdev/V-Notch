#include "vnotch_common.h"
#include <windows.h>
#include <cstdint>
#include <string>
#include <string_view>

#pragma comment(lib, "advapi32.lib")

namespace vnotch::services {

class StartupManager final {
public:
    static constexpr const wchar_t* kRegistryKeyPath = L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
    static constexpr const wchar_t* kAppName = L"V-Notch";

    static bool IsAutoStartEnabled() noexcept {
        HKEY hkey = nullptr;
        if (::RegOpenKeyExW(HKEY_CURRENT_USER, kRegistryKeyPath, 0, KEY_QUERY_VALUE, &hkey) != ERROR_SUCCESS) {
            return false;
        }
        DWORD type = 0;
        DWORD size = 0;
        LSTATUS status = ::RegQueryValueExW(hkey, kAppName, nullptr, &type, nullptr, &size);
        ::RegCloseKey(hkey);
        return status == ERROR_SUCCESS;
    }

    static int32_t SetAutoStart(bool enable, std::u16string_view exe_path) noexcept {
        HKEY hkey = nullptr;
        LSTATUS open_status = ::RegOpenKeyExW(HKEY_CURRENT_USER, kRegistryKeyPath, 0, KEY_SET_VALUE, &hkey);
        if (open_status != ERROR_SUCCESS) {
            return static_cast<int32_t>(open_status);
        }

        LSTATUS status = ERROR_SUCCESS;
        if (enable) {
            std::wstring quoted;
            quoted.reserve(exe_path.size() + 3);
            quoted.push_back(L'"');
            quoted.append(reinterpret_cast<const wchar_t*>(exe_path.data()), exe_path.size());
            quoted.push_back(L'"');
            DWORD byte_len = static_cast<DWORD>((quoted.size() + 1) * sizeof(wchar_t));
            status = ::RegSetValueExW(
                hkey, kAppName, 0, REG_SZ,
                reinterpret_cast<const BYTE*>(quoted.c_str()), byte_len);
        } else {
            status = ::RegDeleteValueW(hkey, kAppName);
            if (status == ERROR_FILE_NOT_FOUND) status = ERROR_SUCCESS;
        }

        ::RegCloseKey(hkey);
        return static_cast<int32_t>(status);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_startup_is_enabled(uint8_t* out_enabled) noexcept {
    if (!out_enabled) return VN_ERR_INVALID_ARG;
    *out_enabled = vnotch::services::StartupManager::IsAutoStartEnabled() ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_startup_set_enabled(
    uint8_t enable, const char16_t* exe_path, int32_t exe_path_len, int32_t* out_win32_error) noexcept
{
    if (!out_win32_error) return VN_ERR_INVALID_ARG;
    *out_win32_error = vnotch::services::StartupManager::SetAutoStart(
        enable != 0, vnotch::make_u16_view(exe_path, exe_path_len));
    return VN_OK;
}

} // extern "C"
