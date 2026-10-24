#include "../vnotch_common.h"
#include <algorithm>

namespace vnotch::services {

class SpotifyHttpResponseReader final {
public:
    static void ResolveInitialBufferCapacity(
        bool has_content_length,
        int64_t content_length,
        int32_t max_response_bytes,
        int32_t* out_initial_capacity,
        bool* out_exceeds_limit) noexcept {
        if (has_content_length && content_length > static_cast<int64_t>(max_response_bytes)) {
            if (out_initial_capacity) *out_initial_capacity = 0;
            if (out_exceeds_limit) *out_exceeds_limit = true;
            return;
        }
        if (out_exceeds_limit) *out_exceeds_limit = false;
        if (out_initial_capacity) {
            if (has_content_length && content_length >= 0) {
                const int64_t capped = std::min<int64_t>(content_length, static_cast<int64_t>(max_response_bytes));
                *out_initial_capacity = static_cast<int32_t>(capped);
            } else {
                *out_initial_capacity = 8192;
            }
        }
    }

    static bool IsTotalReadWithinLimit(int32_t total_read, int32_t max_response_bytes) noexcept {
        return total_read <= max_response_bytes;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_spotify_http_response_reader_resolve_initial_capacity(
    uint8_t has_content_length,
    int64_t content_length,
    int32_t max_response_bytes,
    int32_t* out_initial_capacity,
    uint8_t* out_exceeds_limit) noexcept {
    if (!out_initial_capacity || !out_exceeds_limit) return VN_E_INVALID_ARG;
    bool exceeds = false;
    vnotch::services::SpotifyHttpResponseReader::ResolveInitialBufferCapacity(
        has_content_length != 0, content_length, max_response_bytes, out_initial_capacity, &exceeds);
    *out_exceeds_limit = exceeds ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_http_response_reader_is_total_within_limit(
    int32_t total_read,
    int32_t max_response_bytes,
    uint8_t* out_within_limit) noexcept {
    if (!out_within_limit) return VN_E_INVALID_ARG;
    *out_within_limit = vnotch::services::SpotifyHttpResponseReader::IsTotalReadWithinLimit(
        total_read, max_response_bytes) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
