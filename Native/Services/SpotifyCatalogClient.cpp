#include "../vnotch_common.h"
#include <algorithm>
#include <string_view>

namespace vnotch::services {

class SpotifyCatalogClient final {
public:
    static int32_t BuildSearchQuery(
        std::u16string_view track_name,
        std::u16string_view artist_name,
        char16_t* out_buf,
        int32_t out_cap) noexcept {
        std::u16string_view t = vnotch::vn_trim(track_name);
        std::u16string_view a = vnotch::vn_trim(artist_name);

        int32_t written = 0;
        const int32_t t_copy = std::min(static_cast<int32_t>(t.size()), out_cap);
        for (int32_t i = 0; i < t_copy; ++i) {
            out_buf[written++] = t[static_cast<size_t>(i)];
        }
        if (!a.empty() && written < out_cap) {
            out_buf[written++] = u' ';
            const int32_t a_copy = std::min(static_cast<int32_t>(a.size()), out_cap - written);
            for (int32_t i = 0; i < a_copy; ++i) {
                out_buf[written++] = a[static_cast<size_t>(i)];
            }
        }
        return written;
    }

    static constexpr bool IsQueryMetadataRejectedStatus(int32_t status_code) noexcept {
        return status_code == 400 || status_code == 404 || status_code == 412;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_spotify_catalog_client_build_query(
    const char16_t* track_name,
    int32_t track_len,
    const char16_t* artist_name,
    int32_t artist_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_buf || out_cap < 0 || !out_len || track_len < 0 || artist_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view t_sv(track_name ? track_name : u"", static_cast<size_t>(track_len));
    std::u16string_view a_sv(artist_name ? artist_name : u"", static_cast<size_t>(artist_len));
    *out_len = vnotch::services::SpotifyCatalogClient::BuildSearchQuery(t_sv, a_sv, out_buf, out_cap);
    return VN_OK;
}

VN_EXPORT int32_t vn_spotify_catalog_client_is_metadata_rejected_status(
    int32_t status_code,
    uint8_t* out_rejected) noexcept {
    if (!out_rejected) return VN_E_INVALID_ARG;
    *out_rejected = vnotch::services::SpotifyCatalogClient::IsQueryMetadataRejectedStatus(status_code) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
