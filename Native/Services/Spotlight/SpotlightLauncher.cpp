#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "vnotch_common.h"
#include <objbase.h>
#include <shlobj.h>
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")

namespace vnotch::services::spotlight {

class SpotlightLauncher final {
public:
    SpotlightLauncher() = delete;

    // 0 = Unknown, 1 = Lock, 2 = RecycleBin, 3 = Restart, 4 = Shutdown, 5 = SignOut, 6 = Hibernate, 7 = AdvancedRestart
    [[nodiscard]] static constexpr int32_t ClassifySystemAction(
        std::u16string_view target,
        int32_t& out_wait_ms) noexcept {
        out_wait_ms = 3000;
        if (target == u"vnotch-action:lock") return 1;
        if (target == u"shell:RecycleBinFolder") return 2;
        if (target == u"vnotch-action:restart") return 3;
        if (target == u"vnotch-action:shutdown") return 4;
        if (target == u"vnotch-action:signOut") return 5;
        if (target == u"vnotch-action:hibernate") {
            out_wait_ms = 250;
            return 6;
        }
        if (target == u"vnotch-action:advancedRestart") return 7;
        return 0;
    }

    [[nodiscard]] static constexpr bool IsTargetStringSafe(std::u16string_view target) noexcept {
        if (vn_trim(target).empty()) return false;
        for (const char16_t ch : target) {
            if (ch == u'\0' || ch == u'\r' || ch == u'\n') return false;
        }
        return true;
    }

    [[nodiscard]] static constexpr bool IsAppsFolderTarget(std::u16string_view target) noexcept {
        constexpr std::u16string_view kPrefix = u"shell:AppsFolder\\";
        return target.size() > kPrefix.size() && vn_starts_with_icase_ascii(target, kPrefix);
    }
};

} // namespace vnotch::services::spotlight

extern "C" {

VN_EXPORT int32_t vn_spotlight_launcher_classify_system_action(
    const char16_t* target,
    int32_t target_len,
    int32_t* out_action,
    int32_t* out_wait_ms) noexcept {
    if (!out_action || !out_wait_ms || (!target && target_len > 0) || target_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(target ? target : u"", static_cast<size_t>(target_len));
    int32_t wait_ms = 0;
    *out_action = vnotch::services::spotlight::SpotlightLauncher::ClassifySystemAction(sv, wait_ms);
    *out_wait_ms = wait_ms;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_launcher_is_target_string_safe(
    const char16_t* target,
    int32_t target_len,
    uint8_t* out_safe) noexcept {
    if (!out_safe || (!target && target_len > 0) || target_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(target ? target : u"", static_cast<size_t>(target_len));
    *out_safe = vnotch::services::spotlight::SpotlightLauncher::IsTargetStringSafe(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_launcher_is_apps_folder_target(
    const char16_t* target,
    int32_t target_len,
    uint8_t* out_is_apps_folder) noexcept {
    if (!out_is_apps_folder || (!target && target_len > 0) || target_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(target ? target : u"", static_cast<size_t>(target_len));
    *out_is_apps_folder = vnotch::services::spotlight::SpotlightLauncher::IsAppsFolderTarget(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_spotlight_launcher_reveal_in_explorer(
    const char16_t* path,
    int32_t path_len) noexcept {
    if (!path || path_len <= 0 || path_len >= 32768) return VN_E_INVALID_ARG;
    const std::u16string_view sv(path, static_cast<size_t>(path_len));
    if (!vnotch::services::spotlight::SpotlightLauncher::IsTargetStringSafe(sv)) return VN_E_INVALID_ARG;

    try {
        wchar_t stack_buf[1024];
        std::wstring heap_buf;
        const wchar_t* c_path = nullptr;
        if (sv.size() < std::size(stack_buf)) {
            std::memcpy(stack_buf, sv.data(), sv.size() * sizeof(wchar_t));
            stack_buf[sv.size()] = L'\0';
            c_path = stack_buf;
        } else {
            heap_buf.assign(reinterpret_cast<const wchar_t*>(sv.data()), sv.size());
            c_path = heap_buf.c_str();
        }

        const HRESULT hr_co = ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
        const bool should_uninit = SUCCEEDED(hr_co);
        if (FAILED(hr_co) && hr_co != RPC_E_CHANGED_MODE) return VN_ERR_FAIL;

        PIDLIST_ABSOLUTE pidl = nullptr;
        HRESULT hr = ::SHParseDisplayName(c_path, nullptr, &pidl, 0, nullptr);
        if (SUCCEEDED(hr) && pidl != nullptr) {
            hr = ::SHOpenFolderAndSelectItems(pidl, 0, nullptr, 0);
            ::ILFree(pidl);
        }

        if (should_uninit) ::CoUninitialize();
        return SUCCEEDED(hr) ? VN_OK : VN_ERR_FAIL;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
