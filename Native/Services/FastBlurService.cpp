#include "vnotch_common.h"

namespace vnotch::services
{
    class FastBlurService final
    {
    public:
        static constexpr uint32_t GetWindowReciprocal(int32_t window) noexcept
        {
            uint32_t w = static_cast<uint32_t>(window);
            return ((1u << 24) + w - 1u) / w;
        }

        static void DarkenPixels(uint8_t* pixels, int32_t length, float factor) noexcept
        {
            for (int32_t i = 0; i + 2 < length; i += 4)
            {
                pixels[i] = static_cast<uint8_t>(pixels[i] * factor);
                pixels[i + 1] = static_cast<uint8_t>(pixels[i + 1] * factor);
                pixels[i + 2] = static_cast<uint8_t>(pixels[i + 2] * factor);
            }
        }

        static void BoxBlurHorizontal(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
        {
            int32_t window = 2 * radius + 1;
            uint32_t reciprocal = GetWindowReciprocal(window);
            int32_t last_x = w - 1;

            for (int32_t y = 0; y < h; ++y)
            {
                int32_t p_base = y * w * 4;
                int32_t first_edge_count = radius + 1;
                int32_t sum_b = source[p_base] * first_edge_count;
                int32_t sum_g = source[p_base + 1] * first_edge_count;
                int32_t sum_r = source[p_base + 2] * first_edge_count;
                int32_t sum_a = source[p_base + 3] * first_edge_count;

                for (int32_t dx = 1; dx <= radius; ++dx)
                {
                    int32_t nx = dx < last_x ? dx : last_x;
                    int32_t o = p_base + nx * 4;
                    sum_b += source[o];
                    sum_g += source[o + 1];
                    sum_r += source[o + 2];
                    sum_a += source[o + 3];
                }

                if (w > window)
                {
                    int32_t x = 0;
                    int32_t right_limit = w - radius - 1;
                    for (; x < radius; ++x)
                    {
                        int32_t t = p_base + x * 4;
                        target[t] = static_cast<uint8_t>((static_cast<uint32_t>(sum_b) * reciprocal) >> 24);
                        target[t + 1] = static_cast<uint8_t>((static_cast<uint32_t>(sum_g) * reciprocal) >> 24);
                        target[t + 2] = static_cast<uint8_t>((static_cast<uint32_t>(sum_r) * reciprocal) >> 24);
                        target[t + 3] = static_cast<uint8_t>((static_cast<uint32_t>(sum_a) * reciprocal) >> 24);

                        int32_t o_in = p_base + (x + 1 + radius) * 4;
                        sum_b += source[o_in] - source[p_base];
                        sum_g += source[o_in + 1] - source[p_base + 1];
                        sum_r += source[o_in + 2] - source[p_base + 2];
                        sum_a += source[o_in + 3] - source[p_base + 3];
                    }

                    for (; x < right_limit; ++x)
                    {
                        int32_t t = p_base + x * 4;
                        target[t] = static_cast<uint8_t>((static_cast<uint32_t>(sum_b) * reciprocal) >> 24);
                        target[t + 1] = static_cast<uint8_t>((static_cast<uint32_t>(sum_g) * reciprocal) >> 24);
                        target[t + 2] = static_cast<uint8_t>((static_cast<uint32_t>(sum_r) * reciprocal) >> 24);
                        target[t + 3] = static_cast<uint8_t>((static_cast<uint32_t>(sum_a) * reciprocal) >> 24);

                        int32_t o_out = p_base + (x - radius) * 4;
                        int32_t o_in = p_base + (x + 1 + radius) * 4;
                        sum_b += source[o_in] - source[o_out];
                        sum_g += source[o_in + 1] - source[o_out + 1];
                        sum_r += source[o_in + 2] - source[o_out + 2];
                        sum_a += source[o_in + 3] - source[o_out + 3];
                    }

                    int32_t o_last = p_base + last_x * 4;
                    for (; x < w; ++x)
                    {
                        int32_t t = p_base + x * 4;
                        target[t] = static_cast<uint8_t>((static_cast<uint32_t>(sum_b) * reciprocal) >> 24);
                        target[t + 1] = static_cast<uint8_t>((static_cast<uint32_t>(sum_g) * reciprocal) >> 24);
                        target[t + 2] = static_cast<uint8_t>((static_cast<uint32_t>(sum_r) * reciprocal) >> 24);
                        target[t + 3] = static_cast<uint8_t>((static_cast<uint32_t>(sum_a) * reciprocal) >> 24);

                        int32_t o_out = p_base + (x - radius) * 4;
                        sum_b += source[o_last] - source[o_out];
                        sum_g += source[o_last + 1] - source[o_out + 1];
                        sum_r += source[o_last + 2] - source[o_out + 2];
                        sum_a += source[o_last + 3] - source[o_out + 3];
                    }
                }
                else
                {
                    for (int32_t x = 0; x < w; ++x)
                    {
                        int32_t t = p_base + x * 4;
                        target[t] = static_cast<uint8_t>((static_cast<uint32_t>(sum_b) * reciprocal) >> 24);
                        target[t + 1] = static_cast<uint8_t>((static_cast<uint32_t>(sum_g) * reciprocal) >> 24);
                        target[t + 2] = static_cast<uint8_t>((static_cast<uint32_t>(sum_r) * reciprocal) >> 24);
                        target[t + 3] = static_cast<uint8_t>((static_cast<uint32_t>(sum_a) * reciprocal) >> 24);

                        int32_t out_x = std::clamp(x - radius, 0, last_x);
                        int32_t in_x = std::clamp(x + 1 + radius, 0, last_x);

                        int32_t o_out = p_base + out_x * 4;
                        int32_t o_in = p_base + in_x * 4;
                        sum_b += source[o_in] - source[o_out];
                        sum_g += source[o_in + 1] - source[o_out + 1];
                        sum_r += source[o_in + 2] - source[o_out + 2];
                        sum_a += source[o_in + 3] - source[o_out + 3];
                    }
                }
            }
        }

        static void BoxBlurVertical(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius)
        {
            int32_t window = 2 * radius + 1;
            uint32_t reciprocal = GetWindowReciprocal(window);
            int32_t row_stride = w * 4;

            int32_t stack_sums[1024];
            std::vector<int32_t> heap_sums;
            int32_t* sums = stack_sums;
            if (row_stride > 1024)
            {
                heap_sums.resize(static_cast<size_t>(row_stride));
                sums = heap_sums.data();
            }

            int32_t first_edge_count = radius + 1;
            for (int32_t c = 0; c < row_stride; ++c)
            {
                sums[c] = source[c] * first_edge_count;
            }

            for (int32_t dy = 1; dy <= radius; ++dy)
            {
                int32_t row = std::clamp(dy, 0, h - 1) * row_stride;
                for (int32_t c = 0; c < row_stride; ++c)
                {
                    sums[c] += source[row + c];
                }
            }

            for (int32_t y = 0; y < h; ++y)
            {
                int32_t row = y * row_stride;
                int32_t out_row = std::clamp(y - radius, 0, h - 1) * row_stride;
                int32_t in_row = std::clamp(y + radius + 1, 0, h - 1) * row_stride;
                for (int32_t c = 0; c < row_stride; ++c)
                {
                    target[row + c] = static_cast<uint8_t>((static_cast<uint32_t>(sums[c]) * reciprocal) >> 24);
                    sums[c] += source[in_row + c] - source[out_row + c];
                }
            }
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_fast_blur_get_window_reciprocal(int32_t window, uint32_t* out_reciprocal) noexcept
{
    if (out_reciprocal == nullptr || window <= 0)
        return VN_ERR_INVALID_ARG;
    *out_reciprocal = vnotch::services::FastBlurService::GetWindowReciprocal(window);
    return VN_OK;
}

VN_EXPORT int32_t vn_fast_blur_darken_pixels(uint8_t* pixels, int32_t length, float factor) noexcept
{
    if (pixels == nullptr && length > 0)
        return VN_ERR_INVALID_ARG;
    vnotch::services::FastBlurService::DarkenPixels(pixels, length, factor);
    return VN_OK;
}

VN_EXPORT int32_t vn_fast_blur_box_horizontal(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
{
    if (!source || !target || w <= 0 || h <= 0 || radius < 0)
        return VN_ERR_INVALID_ARG;
    vnotch::services::FastBlurService::BoxBlurHorizontal(source, target, w, h, radius);
    return VN_OK;
}

VN_EXPORT int32_t vn_fast_blur_box_vertical(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
{
    if (!source || !target || w <= 0 || h <= 0 || radius < 0)
        return VN_ERR_INVALID_ARG;
    vnotch::services::FastBlurService::BoxBlurVertical(source, target, w, h, radius);
    return VN_OK;
}
