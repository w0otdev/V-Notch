#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class NotchShellState final {
public:
    static constexpr bool IsEffectivelyNotchVisible(bool is_notch_visible, bool is_hidden_by_fullscreen, bool is_hidden_by_idle) noexcept {
        return is_notch_visible && !is_hidden_by_fullscreen && !is_hidden_by_idle;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_notchshell_is_effectively_visible(
    uint8_t is_notch_visible, uint8_t is_hidden_by_fullscreen, uint8_t is_hidden_by_idle,
    uint8_t* out_visible) noexcept
{
    if (!out_visible) return VN_ERR_INVALID_ARG;
    *out_visible = vnotch::services::NotchShellState::IsEffectivelyNotchVisible(
        is_notch_visible != 0, is_hidden_by_fullscreen != 0, is_hidden_by_idle != 0) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
