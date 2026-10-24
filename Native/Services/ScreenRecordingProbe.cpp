#include "../vnotch_common.h"

namespace vnotch::services {

class ScreenRecordingProbe final {
public:
    static bool ShouldCaptureOwnedSnapshot(int32_t process_provider_count, bool has_get_snapshot_delegate) noexcept {
        return process_provider_count > 0 && !has_get_snapshot_delegate;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_screen_recording_probe_should_capture_owned_snapshot(
    int32_t process_provider_count,
    uint8_t has_get_snapshot_delegate,
    uint8_t* out_should_capture) noexcept {
    if (!out_should_capture) return VN_E_INVALID_ARG;
    *out_should_capture = vnotch::services::ScreenRecordingProbe::ShouldCaptureOwnedSnapshot(
        process_provider_count, has_get_snapshot_delegate != 0) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
