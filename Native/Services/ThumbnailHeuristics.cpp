#include "vnotch_common.h"
#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <span>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnSmtcThumbnailInputs {
    uint8_t is_youtube_like_source;
    uint8_t is_soundcloud_source;
    uint8_t is_browser_or_youtube_platform;
    uint8_t is_browser_session;
    uint8_t track_changed;
    uint8_t browser_session_changed;
    uint8_t has_verified_youtube_thumb;
    uint8_t has_verified_soundcloud_thumb;
    uint8_t likely_soundcloud_artwork;
    uint8_t recent_track_change;
    uint8_t cached_thumbnail_is_null;
    int32_t pixel_width;
    int32_t pixel_height;
};
#pragma pack(pop)

class ThumbnailHeuristics final {
public:
    static bool HasPlaceholderProfile(std::span<const uint8_t> pixels, int32_t pixel_count, bool reject_simple_color_icons) noexcept {
        if (pixel_count <= 0) return false;

        std::array<uint64_t, 8> bins{};
        uint64_t lum_bins = 0;
        double saturation_sum = 0.0;
        int32_t near_gray_count = 0;
        int32_t bright_count = 0;
        int32_t buf_len = static_cast<int32_t>(pixels.size());

        for (int32_t i = 0; i + 3 < buf_len; i += 4) {
            uint8_t b = pixels[i];
            uint8_t g = pixels[i + 1];
            uint8_t r = pixels[i + 2];

            int32_t max_c = std::max<int32_t>(r, std::max<int32_t>(g, b));
            int32_t min_c = std::min<int32_t>(r, std::min<int32_t>(g, b));
            int32_t delta = max_c - min_c;

            if (delta <= 12) near_gray_count++;
            if (max_c >= 220 && min_c >= 220) bright_count++;

            int32_t bin = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
            bins[bin >> 6] |= 1ULL << (bin & 63);
            int32_t lum = (r * 77 + g * 150 + b * 29) >> 10;
            lum_bins |= 1ULL << (lum & 63);
            saturation_sum += max_c == 0 ? 0.0 : static_cast<double>(delta) / max_c;
        }

        int32_t unique_bin_count = 0;
        for (size_t i = 0; i < bins.size(); i++) {
            unique_bin_count += std::popcount(bins[i]);
        }

        int32_t unique_lum_bins = std::popcount(lum_bins);
        double avg_saturation = saturation_sum / pixel_count;
        double gray_ratio = static_cast<double>(near_gray_count) / pixel_count;
        double bright_ratio = static_cast<double>(bright_count) / pixel_count;

        return (reject_simple_color_icons && unique_bin_count <= 18) ||
               (unique_bin_count <= 18 &&
                unique_lum_bins <= 12 &&
                avg_saturation <= 0.08 &&
                gray_ratio >= 0.84 &&
                bright_ratio >= 0.02);
    }

    static int32_t DecideSmtcThumbnail(const VnSmtcThumbnailInputs& x) noexcept {
        double aspect = x.pixel_height == 0 ? 0.0 : static_cast<double>(x.pixel_width) / x.pixel_height;
        if (ShouldRejectSmtcThumbnail(x, aspect)) {
            return 0; // Reject
        }

        bool is_square = std::abs(aspect - 1.0) < 0.05;
        bool should_prefer_verified_youtube_lookup = (x.is_youtube_like_source != 0) &&
                                                     (x.has_verified_youtube_thumb == 0) &&
                                                     (x.track_changed == 0);

        if (!IsGenericOrPlaceholderIcon(x, is_square) && !should_prefer_verified_youtube_lookup) {
            return 1; // Accept
        }

        return 2; // Skip
    }

private:
    static bool ShouldRejectSmtcThumbnail(const VnSmtcThumbnailInputs& x, double aspect) noexcept {
        if ((x.is_soundcloud_source != 0) &&
            (x.has_verified_soundcloud_thumb == 0) &&
            (x.likely_soundcloud_artwork == 0) &&
            ((x.track_changed != 0) || ((x.recent_track_change != 0) && (x.cached_thumbnail_is_null != 0)))) {
            return true;
        }

        if ((x.is_youtube_like_source != 0) &&
            (x.browser_session_changed != 0) &&
            (x.track_changed != 0) &&
            (x.has_verified_youtube_thumb == 0)) {
            return true;
        }

        bool is_wide_video_frame = aspect > 1.3;
        return (x.is_youtube_like_source != 0) &&
               is_wide_video_frame &&
               ((x.track_changed != 0) || ((x.recent_track_change != 0) && (x.cached_thumbnail_is_null != 0)));
    }

    static bool IsGenericOrPlaceholderIcon(const VnSmtcThumbnailInputs& x, bool is_square) noexcept {
        if ((x.likely_soundcloud_artwork != 0) && (x.is_youtube_like_source == 0))
            return false;

        if (!is_square) {
            return false;
        }

        bool is_likely_soundcloud_placeholder = (x.is_soundcloud_source != 0) &&
                                                (x.has_verified_soundcloud_thumb == 0) &&
                                                (x.likely_soundcloud_artwork == 0);
        if (is_likely_soundcloud_placeholder) {
            return true;
        }

        bool is_unverified_browser_icon = (x.is_browser_session != 0) &&
                                          (x.is_browser_or_youtube_platform != 0) &&
                                          (x.has_verified_youtube_thumb == 0);

        return (x.is_browser_or_youtube_platform != 0) &&
               (x.pixel_width <= 300 || is_unverified_browser_icon);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_thumbnail_has_placeholder_profile(
    const uint8_t* pixels, int32_t buf_len, int32_t pixel_count, uint8_t reject_simple_color_icons,
    uint8_t* out_has_profile) noexcept
{
    if (!out_has_profile) return VN_ERR_INVALID_ARG;
    if (!pixels || buf_len <= 0 || pixel_count <= 0) {
        *out_has_profile = 0;
        return VN_OK;
    }
    *out_has_profile = vnotch::services::ThumbnailHeuristics::HasPlaceholderProfile(
        std::span<const uint8_t>(pixels, static_cast<size_t>(buf_len)),
        pixel_count,
        reject_simple_color_icons != 0) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_thumbnail_decide_smtc(
    const vnotch::services::VnSmtcThumbnailInputs* inputs, int32_t* out_decision) noexcept
{
    if (!inputs || !out_decision) return VN_ERR_INVALID_ARG;
    *out_decision = vnotch::services::ThumbnailHeuristics::DecideSmtcThumbnail(*inputs);
    return VN_OK;
}

} // extern "C"
