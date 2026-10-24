#include "../vnotch_common.h"

namespace vnotch::services {

class LocalizedPresentation final {
public:
    static int32_t ResolveFlowDirectionValue(bool is_right_to_left) noexcept {
        return is_right_to_left ? 1 : 0;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_localized_presentation_resolve_flow_direction(
    uint8_t is_right_to_left,
    int32_t* out_flow_direction) noexcept {
    if (!out_flow_direction) return VN_E_INVALID_ARG;
    *out_flow_direction = vnotch::services::LocalizedPresentation::ResolveFlowDirectionValue(is_right_to_left != 0);
    return VN_OK;
}

} // extern "C"
