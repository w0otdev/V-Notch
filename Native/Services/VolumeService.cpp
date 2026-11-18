#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services {

class VolumeService final {
public:
    static float ClampVolumeScalar(float volume) noexcept {
        if (!std::isfinite(volume)) return 0.0f;
        return std::clamp(volume, 0.0f, 1.0f);
    }

};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_volume_service_clamp_scalar(
    float volume,
    float* out_clamped) noexcept {
    if (!out_clamped) return VN_E_INVALID_ARG;
    *out_clamped = vnotch::services::VolumeService::ClampVolumeScalar(volume);
    return VN_OK;
}

} // extern "C"
