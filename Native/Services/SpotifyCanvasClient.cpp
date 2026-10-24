#include "../vnotch_common.h"

namespace vnotch::services {

class SpotifyCanvasClient final {
public:
    static constexpr bool IsQueryMetadataRejectedStatus(int32_t status_code) noexcept {
        return status_code == 400 || status_code == 404 || status_code == 412;
    }

    static constexpr bool ShouldRefreshCanvasMetadata(
        bool query_metadata_rejected,
        bool has_json,
        bool persisted_query_not_found) noexcept {
        return query_metadata_rejected || (has_json && persisted_query_not_found);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_spotify_canvas_client_is_metadata_rejected_status(
    int32_t status_code,
    uint8_t* out_rejected) noexcept {
    if (!out_rejected) return VN_E_INVALID_ARG;
    *out_rejected = vnotch::services::SpotifyCanvasClient::IsQueryMetadataRejectedStatus(status_code) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_canvas_client_should_refresh_metadata(
    uint8_t query_metadata_rejected,
    uint8_t has_json,
    uint8_t persisted_query_not_found,
    uint8_t* out_refresh) noexcept {
    if (!out_refresh) return VN_E_INVALID_ARG;
    *out_refresh = vnotch::services::SpotifyCanvasClient::ShouldRefreshCanvasMetadata(
        query_metadata_rejected != 0, has_json != 0, persisted_query_not_found != 0) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
