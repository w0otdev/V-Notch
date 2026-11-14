#include "../vnotch_common.h"
#include "../vnotch_cpu.h"
#include "../vnotch_simd_kernels.h"
#include <algorithm>
#include <cstdint>
#include <cstring>

namespace vnotch::controllers
{

class GlassBoxBlur final
{
    struct ExactByteAverage
    {
        uint32_t divisor;
        uint64_t reciprocal;

        constexpr explicit ExactByteAverage(int32_t d) noexcept
            : divisor(static_cast<uint32_t>(d))
            , reciprocal(((1ULL << 32) + static_cast<uint32_t>(d) - 1ULL) / static_cast<uint32_t>(d))
        {
        }

        __forceinline uint8_t divide(int32_t sum) const noexcept
        {
            uint32_t u = static_cast<uint32_t>(sum);
            uint32_t q = static_cast<uint32_t>((static_cast<uint64_t>(u) * reciprocal) >> 32);
            if (q * divisor > u)
            {
                --q;
            }
            return static_cast<uint8_t>(q);
        }
    };

    // Window limit shared with the vertical pass: sums stay below 2^24, so the
    // float divide-and-truncate is exact for every byte average.
    static constexpr int32_t kMaxFloatWindow = 4097;

    // Keeps 2 * radius + 1, per-pixel sums and the byte offsets (w * 4 as int32 column indexes) inside their types.
    static constexpr int32_t kMaxRadius = 1 << 20;
    static constexpr int32_t kMaxDimension = 1 << 24;

    static int32_t HorizontalScalar(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t radius,
        int32_t y0,
        int32_t y1) noexcept
    {
        ExactByteAverage divisor(2 * radius + 1);
        int32_t left_end = std::min(w, radius);
        int32_t right_start = std::max(left_end, w - radius - 1);

        for (int32_t y = y0; y < y1; ++y)
        {
            const size_t row_base = static_cast<size_t>(y) * static_cast<size_t>(w) * 4u;
            int32_t sum_b = 0, sum_g = 0, sum_r = 0, sum_a = 0;
            for (int32_t dx = -radius; dx <= radius; ++dx)
            {
                const size_t o = row_base + static_cast<size_t>(std::clamp(dx, 0, w - 1)) * 4u;
                sum_b += src[o];
                sum_g += src[o + 1];
                sum_r += src[o + 2];
                sum_a += src[o + 3];
            }

            int32_t x = 0;
            for (; x < left_end; ++x)
            {
                const size_t t = row_base + static_cast<size_t>(x) * 4u;
                dst[t] = divisor.divide(sum_b);
                dst[t + 1] = divisor.divide(sum_g);
                dst[t + 2] = divisor.divide(sum_r);
                dst[t + 3] = divisor.divide(sum_a);

                const int32_t in_x = std::min(w - 1, x + 1 + radius);
                const size_t o_out = row_base;
                const size_t o_in = row_base + static_cast<size_t>(in_x) * 4u;
                sum_b += src[o_in] - src[o_out];
                sum_g += src[o_in + 1] - src[o_out + 1];
                sum_r += src[o_in + 2] - src[o_out + 2];
                sum_a += src[o_in + 3] - src[o_out + 3];
            }

            for (; x < right_start; ++x)
            {
                const size_t t = row_base + static_cast<size_t>(x) * 4u;
                dst[t] = divisor.divide(sum_b);
                dst[t + 1] = divisor.divide(sum_g);
                dst[t + 2] = divisor.divide(sum_r);
                dst[t + 3] = divisor.divide(sum_a);

                const size_t o_out = t - static_cast<size_t>(radius) * 4u;
                const size_t o_in = t + (static_cast<size_t>(radius) + 1u) * 4u;
                sum_b += src[o_in] - src[o_out];
                sum_g += src[o_in + 1] - src[o_out + 1];
                sum_r += src[o_in + 2] - src[o_out + 2];
                sum_a += src[o_in + 3] - src[o_out + 3];
            }

            const size_t last_pixel_offset = row_base + static_cast<size_t>(w - 1) * 4u;
            for (; x < w; ++x)
            {
                const size_t t = row_base + static_cast<size_t>(x) * 4u;
                dst[t] = divisor.divide(sum_b);
                dst[t + 1] = divisor.divide(sum_g);
                dst[t + 2] = divisor.divide(sum_r);
                dst[t + 3] = divisor.divide(sum_a);

                const int32_t out_x = std::max(0, x - radius);
                const size_t o_out = row_base + static_cast<size_t>(out_x) * 4u;
                const size_t o_in = last_pixel_offset;
                sum_b += src[o_in] - src[o_out];
                sum_g += src[o_in + 1] - src[o_out + 1];
                sum_r += src[o_in + 2] - src[o_out + 2];
                sum_a += src[o_in + 3] - src[o_out + 3];
            }
        }

        return VN_OK;
    }

public:
    static int32_t Horizontal(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t radius,
        int32_t y0,
        int32_t y1) noexcept
    {
        if (src == nullptr || dst == nullptr || w <= 0 || w > kMaxDimension
            || radius < 0 || radius > kMaxRadius || y0 < 0 || y1 < y0)
        {
            return VN_ERR_INVALID_ARG;
        }

        if (2 * radius + 1 <= kMaxFloatWindow && vnotch::cpu::SupportsX64V3())
        {
            vnotch::kernels::avx2::GlassBoxBlurHorizontal(src, dst, w, radius, y0, y1);
            return VN_OK;
        }
        return HorizontalScalar(src, dst, w, radius, y0, y1);
    }

    static int32_t Vertical(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t h,
        int32_t radius,
        int32_t x0,
        int32_t x1) noexcept
    {
        if (src == nullptr || dst == nullptr || w <= 0 || w > kMaxDimension || h <= 0 || h > kMaxDimension
            || radius < 0 || radius > kMaxRadius || x0 < 0 || x1 < x0 || x1 > w)
        {
            return VN_ERR_INVALID_ARG;
        }

        const int32_t window = 2 * radius + 1;
        const size_t stride = static_cast<size_t>(w) * 4u;
        int32_t column = x0 * 4;
        const int32_t end = x1 * 4;

        if (window <= kMaxFloatWindow && vnotch::cpu::SupportsX64V3())
        {
            column = vnotch::kernels::avx2::GlassBoxBlurVerticalBulk(src, dst, w, h, radius, column, end);
        }

        ExactByteAverage scalar_divisor(window);
        for (; column < end; ++column)
        {
            int32_t sum = 0;
            for (int32_t dy = -radius; dy <= radius; ++dy)
            {
                sum += src[static_cast<size_t>(std::clamp(dy, 0, h - 1)) * stride + static_cast<size_t>(column)];
            }
            for (int32_t y = 0; y < h; ++y)
            {
                dst[static_cast<size_t>(y) * stride + static_cast<size_t>(column)] = scalar_divisor.divide(sum);
                sum += src[static_cast<size_t>(std::min(h - 1, y + radius + 1)) * stride + static_cast<size_t>(column)]
                     - src[static_cast<size_t>(std::max(0, y - radius)) * stride + static_cast<size_t>(column)];
            }
        }

        return VN_OK;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t vn_glass_box_blur_horizontal(
    const uint8_t* src,
    uint8_t* dst,
    int32_t w,
    int32_t radius,
    int32_t y0,
    int32_t y1) noexcept
{
    return vnotch::controllers::GlassBoxBlur::Horizontal(src, dst, w, radius, y0, y1);
}

VN_EXPORT int32_t vn_glass_box_blur_vertical(
    const uint8_t* src,
    uint8_t* dst,
    int32_t w,
    int32_t h,
    int32_t radius,
    int32_t x0,
    int32_t x1) noexcept
{
    return vnotch::controllers::GlassBoxBlur::Vertical(src, dst, w, h, radius, x0, x1);
}

} // extern "C"
