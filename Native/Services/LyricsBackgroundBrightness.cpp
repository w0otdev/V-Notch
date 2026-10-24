#include "vnotch_common.h"

namespace vnotch::services
{
    class LyricsBackgroundBrightness final
    {
    public:
        static int32_t BrightenInPlace(uint8_t* pixels, int32_t byte_count, uint8_t* out_modified) noexcept
        {
            if (pixels == nullptr || out_modified == nullptr || byte_count < 0)
            {
                return VN_ERR_INVALID_ARG;
            }
            *out_modified = 0u;
            double luminance = 0.0;
            double weight = 0.0;
            for (int32_t i = 0; i + 3 < byte_count; i += 4)
            {
                double alpha = pixels[i + 3] / 255.0;
                luminance += (0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2]) * alpha;
                weight += alpha;
            }
            if (weight == 0.0 || (luminance / weight) >= 150.0)
            {
                return VN_OK;
            }

            double average = std::max(1.0, luminance / weight);
            double gamma = std::clamp(std::log(150.0 / 255.0) / std::log(average / 255.0), 0.45, 1.0);
            uint8_t lookup[256];
            for (int32_t i = 0; i < 256; ++i)
            {
                lookup[i] = static_cast<uint8_t>(std::nearbyint(255.0 * std::pow(i / 255.0, gamma)));
            }
            for (int32_t i = 0; i + 3 < byte_count; i += 4)
            {
                pixels[i] = lookup[pixels[i]];
                pixels[i + 1] = lookup[pixels[i + 1]];
                pixels[i + 2] = lookup[pixels[i + 2]];
            }
            *out_modified = 1u;
            return VN_OK;
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_lyrics_brightness_apply(uint8_t* pixels, int32_t byte_count, uint8_t* out_modified) noexcept
{
    return vnotch::services::LyricsBackgroundBrightness::BrightenInPlace(pixels, byte_count, out_modified);
}
