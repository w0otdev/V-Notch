#include "../vnotch_common.h"

namespace vnotch::services {

class SettingsEditSession final {
public:
    static bool ShouldApplyPreview(bool value_equals) noexcept {
        return !value_equals;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_settings_edit_session_should_apply_preview(
    uint8_t value_equals,
    uint8_t* out_should_apply) noexcept {
    if (!out_should_apply) return VN_E_INVALID_ARG;
    *out_should_apply = vnotch::services::SettingsEditSession::ShouldApplyPreview(value_equals != 0) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
