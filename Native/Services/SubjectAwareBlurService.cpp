#include "vnotch_common.h"

VN_EXPORT int32_t vn_fast_blur_darken_pixels(uint8_t* pixels, int32_t length, float factor) noexcept;
VN_EXPORT int32_t vn_fast_blur_box_horizontal(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept;
VN_EXPORT int32_t vn_fast_blur_box_vertical(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept;

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
            if (!pixels || buf_len <= 0) return;
            const float safe_factor = std::isfinite(factor) ? std::clamp(factor, 0.0f, 255.0f) : 1.0f;
            for (int32_t i = 0; i + 2 < buf_len; i += 4)
            {
                pixels[i] = static_cast<uint8_t>(std::min(255.0f, pixels[i] * safe_factor));
                pixels[i + 1] = static_cast<uint8_t>(std::min(255.0f, pixels[i + 1] * safe_factor));
                pixels[i + 2] = static_cast<uint8_t>(std::min(255.0f, pixels[i + 2] * safe_factor));
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
            const float safe_cx = std::isfinite(center_x) ? std::clamp(center_x, 0.0f, 1.0f) : 0.5f;
            const float safe_cy = std::isfinite(center_y) ? std::clamp(center_y, 0.0f, 1.0f) : 0.5f;
            const float safe_sw = std::isfinite(sub_width) ? std::clamp(sub_width, 0.0f, 1.0f) : 0.36f;
            const float safe_sh = std::isfinite(sub_height) ? std::clamp(sub_height, 0.0f, 1.0f) : 0.44f;

            float cx = safe_cx * static_cast<float>(width);
            float cy = safe_cy * static_cast<float>(height);
            float rx = std::max(width * 0.18f, safe_sw * width * 0.55f);
            float ry = std::max(height * 0.22f, safe_sh * height * 0.55f);
            constexpr float feather = 0.40f;

            for (int32_t y = 0; y < height; ++y)
            {
                int64_t row = static_cast<int64_t>(y) * stride;
                float dy = (static_cast<float>(y) - cy) / ry;
                float dy_squared = dy * dy;
                for (int32_t x = 0; x < width; ++x)
                {
                    float dx = (static_cast<float>(x) - cx) / rx;
                    float t = ComputeBlendWeight(dx * dx + dy_squared, feather);

                    int64_t p = row + static_cast<int64_t>(x) * 4LL;
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
    if ((!pixels && buf_len > 0) || buf_len < 0) return VN_ERR_INVALID_ARG;
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
    if (!background || !subject_layer || width <= 0 || height <= 0 || width > 16384 || height > 16384 ||
        static_cast<int64_t>(stride) < static_cast<int64_t>(width) * 4LL)
        return VN_ERR_INVALID_ARG;
    vnotch::services::SubjectAwareBlurService::CompositeLayers(
        background, subject_layer, center_x, center_y, sub_width, sub_height, width, height, stride);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_subject_blur_process_bgra32(
    uint8_t* background, uint8_t* subject_layer, uint8_t* scratch,
    int32_t w, int32_t h, int32_t stride,
    float center_x, float center_y, float sub_w, float sub_h,
    int32_t bg_radius, int32_t sub_radius,
    float bg_darken, float sub_brighten) noexcept
{
    if (!background || !subject_layer || !scratch || w <= 0 || h <= 0 || w > 16384 || h > 16384 ||
        static_cast<int64_t>(stride) < static_cast<int64_t>(w) * 4LL ||
        static_cast<int64_t>(stride) * static_cast<int64_t>(h) > INT32_MAX ||
        bg_radius < 0 || bg_radius > 256 || sub_radius < 0 || sub_radius > 256)
        return VN_ERR_INVALID_ARG;

    const int32_t buf_len = stride * h;
    std::memcpy(subject_layer, background, static_cast<size_t>(buf_len));

    if (bg_radius >= 1)
    {
        for (int32_t i = 0; i < 2; ++i)
        {
            vn_fast_blur_box_horizontal(background, scratch, w, h, bg_radius);
            vn_fast_blur_box_vertical(scratch, background, w, h, bg_radius);
        }
    }
    vn_fast_blur_darken_pixels(background, w * h * 4, bg_darken);

    if (sub_radius >= 1)
    {
        vn_fast_blur_box_horizontal(subject_layer, scratch, w, h, sub_radius);
        vn_fast_blur_box_vertical(scratch, subject_layer, w, h, sub_radius);
    }
    vnotch::services::SubjectAwareBlurService::Brighten(subject_layer, buf_len, sub_brighten);

    vnotch::services::SubjectAwareBlurService::CompositeLayers(
        background, subject_layer, center_x, center_y, sub_w, sub_h, w, h, stride);
    return VN_OK;
}
