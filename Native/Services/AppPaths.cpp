#include "../vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class AppPaths final {
public:
    static constexpr bool IsReparsePointAttribute(int32_t attributes) noexcept {
        // FileAttributes.ReparsePoint == 0x400
        return (attributes & 0x400) != 0;
    }

    static constexpr bool IsDirectoryAttribute(int32_t attributes) noexcept {
        // FileAttributes.Directory == 0x10
        return (attributes & 0x10) != 0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_app_paths_is_reparse_point(
    int32_t attributes,
    uint8_t* out_is_reparse) noexcept {
    if (!out_is_reparse) return VN_E_INVALID_ARG;
    *out_is_reparse = vnotch::services::AppPaths::IsReparsePointAttribute(attributes) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_app_paths_is_directory(
    int32_t attributes,
    uint8_t* out_is_dir) noexcept {
    if (!out_is_dir) return VN_E_INVALID_ARG;
    *out_is_dir = vnotch::services::AppPaths::IsDirectoryAttribute(attributes) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
