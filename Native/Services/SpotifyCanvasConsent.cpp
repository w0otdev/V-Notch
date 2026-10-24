#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

class SpotifyCanvasConsent final {
public:
    static constexpr int32_t kCurrentNoticeVersion = 1;

    static constexpr bool HasAccepted(int32_t consent_version) noexcept {
        return consent_version == kCurrentNoticeVersion;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_spotify_canvas_consent_has_accepted(
    int32_t consent_version, uint8_t* out_accepted) noexcept
{
    if (!out_accepted) return VN_ERR_INVALID_ARG;
    *out_accepted = vnotch::services::SpotifyCanvasConsent::HasAccepted(consent_version) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
