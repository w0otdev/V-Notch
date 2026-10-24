#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class SettingsApplicationService final {
public:
    static bool IsNonWhitespaceFilePath(std::u16string_view file_path) noexcept {
        for (char16_t ch : file_path) {
            if (ch > u' ' && ch != 0x00A0 && ch != 0xFEFF) {
                return true;
            }
        }
        return false;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_settings_application_service_is_valid_file_path(
    const char16_t* path_ptr,
    int32_t path_len,
    uint8_t* out_valid) noexcept {
    if (!out_valid) return VN_E_INVALID_ARG;
    std::u16string_view path{};
    if (path_ptr && path_len > 0) {
        path = std::u16string_view(path_ptr, static_cast<size_t>(path_len));
    }
    *out_valid = vnotch::services::SettingsApplicationService::IsNonWhitespaceFilePath(path) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
