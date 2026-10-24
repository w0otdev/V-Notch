#include "vnotch_common.h"

namespace vnotch::services
{
    class SubjectAwareBlurService final
    {
    public:
        static constexpr float ComputeBlendWeight(float distance_squared, float feather) noexcept
        {
            if (distance_squared <= 1.0f)
            {
                return 1.0f;
            }
            float outer = (1.0f + feather) * (1.0f + feather);
            if (distance_squared >= outer)
            {
                return 0.0f;
            }
            float dist = std::sqrt(distance_squared);
            float u = (dist - 1.0f) / feather;
            return 1.0f - u * u * (3.0f - 2.0f * u);
        }

        static void Brighten(uint8_t* pixels, int32_t buf_len, float factor) noexcept
        {
            for (int32_t i = 0; i + 2 < buf_len; i += 4)
            {
                pixels[i] = static_cast<uint8_t>(std::min(255.0f, pixels[i] * factor));
                pixels[i + 1] = static_cast<uint8_t>(std::min(255.0f, pixels[i + 1] * factor));
                pixels[i + 2] = static_cast<uint8_t>(std::min(255.0f, pixels[i + 2] * factor));
            }
        }

        static void CompositeLayers(
            uint8_t* background,
            const uint8_t* subject_layer,
            float center_x,
            float center_y,
            float sub_width,
            float sub_height,
            int32_t width,
            int32_t height,
            int32_t stride) noexcept
        {
            float cx = std::clamp(center_x, 0.0f, 1.0f) * static_cast<float>(width);
            float cy = std::clamp(center_y, 0.0f, 1.0f) * static_cast<float>(height);
            float rx = std::max(width * 0.18f, sub_width * width * 0.55f);
            float ry = std::max(height * 0.22f, sub_height * height * 0.55f);
            constexpr float feather = 0.40f;

            for (int32_t y = 0; y < height; ++y)
            {
                int32_t row = y * stride;
                float dy = (static_cast<float>(y) - cy) / ry;
                float dy_squared = dy * dy;
                for (int32_t x = 0; x < width; ++x)
                {
                    float dx = (static_cast<float>(x) - cx) / rx;
                    float t = ComputeBlendWeight(dx * dx + dy_squared, feather);

                    int32_t p = row + x * 4;
                    uint8_t bb = background[p];
                    uint8_t gb = background[p + 1];
                    uint8_t rb = background[p + 2];
                    uint8_t bs = subject_layer[p];
                    uint8_t gs = subject_layer[p + 1];
                    uint8_t rs = subject_layer[p + 2];

                    background[p] = static_cast<uint8_t>(bb + (bs - bb) * t);
                    background[p + 1] = static_cast<uint8_t>(gb + (gs - gb) * t);
                    background[p + 2] = static_cast<uint8_t>(rb + (rs - rb) * t);
                    background[p + 3] = 255u;
                }
            }
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_subject_blur_brighten(uint8_t* pixels, int32_t buf_len, float factor) noexcept
{
    if (!pixels && buf_len > 0) return VN_ERR_INVALID_ARG;
    vnotch::services::SubjectAwareBlurService::Brighten(pixels, buf_len, factor);
    return VN_OK;
}

VN_EXPORT int32_t vn_subject_blur_composite(
    uint8_t* background,
    const uint8_t* subject_layer,
    float center_x,
    float center_y,
    float sub_width,
    float sub_height,
    int32_t width,
    int32_t height,
    int32_t stride) noexcept
{
    if (!background || !subject_layer || width <= 0 || height <= 0 || stride < width * 4)
        return VN_ERR_INVALID_ARG;
    vnotch::services::SubjectAwareBlurService::CompositeLayers(
        background, subject_layer, center_x, center_y, sub_width, sub_height, width, height, stride);
    return VN_OK;
}
