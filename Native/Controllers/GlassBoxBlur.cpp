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

    static __forceinline void StoreAveragePixel(
        uint8_t* dst,
        const ExactByteAverage& divisor,
        int32_t sum_b,
        int32_t sum_g,
        int32_t sum_r,
        int32_t sum_a) noexcept
    {
        dst[0] = divisor.divide(sum_b);
        dst[1] = divisor.divide(sum_g);
        dst[2] = divisor.divide(sum_r);
        dst[3] = divisor.divide(sum_a);
    }

    static __forceinline void SlideWindowPixel(
        const uint8_t* px_in,
        const uint8_t* px_out,
        int32_t& sum_b,
        int32_t& sum_g,
        int32_t& sum_r,
        int32_t& sum_a) noexcept
    {
        sum_b += static_cast<int32_t>(px_in[0]) - static_cast<int32_t>(px_out[0]);
        sum_g += static_cast<int32_t>(px_in[1]) - static_cast<int32_t>(px_out[1]);
        sum_r += static_cast<int32_t>(px_in[2]) - static_cast<int32_t>(px_out[2]);
        sum_a += static_cast<int32_t>(px_in[3]) - static_cast<int32_t>(px_out[3]);
    }

    static int32_t HorizontalScalar(
        const uint8_t* src,
        uint8_t* dst,
        int32_t w,
        int32_t radius,
        int32_t y0,
        int32_t y1) noexcept
    {
        const ExactByteAverage divisor(2 * radius + 1);
        const int32_t left_end = std::min(w, radius);
        const int32_t right_start = std::max(left_end, w - radius - 1);
        const int32_t init_clamped_right = std::max(0, radius - (w - 1));
        const int32_t init_interior_end = std::min(radius, w - 1);
        const size_t row_bytes = static_cast<size_t>(w) * 4u;

        for (int32_t y = y0; y < y1; ++y)
        {
            const uint8_t* srow = src + static_cast<size_t>(y) * row_bytes;
            uint8_t* drow = dst + static_cast<size_t>(y) * row_bytes;
            const uint8_t* first_px = srow;
            const uint8_t* last_px = srow + static_cast<size_t>(w - 1) * 4u;

            int32_t sum_b = static_cast<int32_t>(first_px[0]) * (radius + 1);
            int32_t sum_g = static_cast<int32_t>(first_px[1]) * (radius + 1);
            int32_t sum_r = static_cast<int32_t>(first_px[2]) * (radius + 1);
            int32_t sum_a = static_cast<int32_t>(first_px[3]) * (radius + 1);

            for (int32_t dx = 1; dx <= init_interior_end; ++dx)
            {
                const uint8_t* px = srow + static_cast<size_t>(dx) * 4u;
                sum_b += px[0];
                sum_g += px[1];
                sum_r += px[2];
                sum_a += px[3];
            }
            if (init_clamped_right > 0)
            {
                sum_b += static_cast<int32_t>(last_px[0]) * init_clamped_right;
                sum_g += static_cast<int32_t>(last_px[1]) * init_clamped_right;
                sum_r += static_cast<int32_t>(last_px[2]) * init_clamped_right;
                sum_a += static_cast<int32_t>(last_px[3]) * init_clamped_right;
            }

            int32_t x = 0;
            for (; x < left_end; ++x)
            {
                StoreAveragePixel(drow + static_cast<size_t>(x) * 4u, divisor, sum_b, sum_g, sum_r, sum_a);
                const int32_t in_x = std::min(w - 1, x + 1 + radius);
                SlideWindowPixel(srow + static_cast<size_t>(in_x) * 4u, first_px, sum_b, sum_g, sum_r, sum_a);
            }

            for (; x < right_start; ++x)
            {
                const size_t px_offset = static_cast<size_t>(x) * 4u;
                StoreAveragePixel(drow + px_offset, divisor, sum_b, sum_g, sum_r, sum_a);
                SlideWindowPixel(
                    srow + px_offset + (static_cast<size_t>(radius) + 1u) * 4u,
                    srow + px_offset - static_cast<size_t>(radius) * 4u,
                    sum_b, sum_g, sum_r, sum_a);
            }

            for (; x < w; ++x)
            {
                StoreAveragePixel(drow + static_cast<size_t>(x) * 4u, divisor, sum_b, sum_g, sum_r, sum_a);
                const int32_t out_x = std::max(0, x - radius);
                SlideWindowPixel(last_px, srow + static_cast<size_t>(out_x) * 4u, sum_b, sum_g, sum_r, sum_a);
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
        if (y0 == y1)
        {
            return VN_OK;
        }
        if (radius == 0)
        {
            const size_t row_bytes = static_cast<size_t>(w) * 4u;
            const size_t offset = static_cast<size_t>(y0) * row_bytes;
            const size_t byte_count = static_cast<size_t>(y1 - y0) * row_bytes;
            if (src != dst)
            {
                std::memmove(dst + offset, src + offset, byte_count);
            }
            return VN_OK;
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
        if (x0 == x1)
        {
            return VN_OK;
        }

        const int32_t window = 2 * radius + 1;
        const size_t stride = static_cast<size_t>(w) * 4u;
        int32_t column = x0 * 4;
        const int32_t end = x1 * 4;

        if (radius == 0)
        {
            if (src != dst)
            {
                const size_t col_offset = static_cast<size_t>(column);
                const size_t span_bytes = static_cast<size_t>(end - column);
                for (int32_t y = 0; y < h; ++y)
                {
                    const size_t row_off = static_cast<size_t>(y) * stride + col_offset;
                    std::memmove(dst + row_off, src + row_off, span_bytes);
                }
            }
            return VN_OK;
        }

        if (window <= kMaxFloatWindow && vnotch::cpu::SupportsX64V3())
        {
            column = vnotch::kernels::avx2::GlassBoxBlurVerticalBulk(src, dst, w, h, radius, column, end);
        }

        const ExactByteAverage scalar_divisor(window);
        const int32_t top_end = std::min(h, radius);
        const int32_t bottom_start = std::max(top_end, h - radius - 1);
        const int32_t init_interior_end = std::min(radius, h - 1);
        const int32_t init_clamped_bottom = std::max(0, radius - (h - 1));
        const size_t last_row_offset = static_cast<size_t>(h - 1) * stride;

        for (; column < end; ++column)
        {
            const uint8_t* scol = src + static_cast<size_t>(column);
            uint8_t* dcol = dst + static_cast<size_t>(column);
            const int32_t top_val = scol[0];
            const int32_t bot_val = scol[last_row_offset];

            int32_t sum = top_val * (radius + 1);
            for (int32_t dy = 1; dy <= init_interior_end; ++dy)
            {
                sum += scol[static_cast<size_t>(dy) * stride];
            }
            if (init_clamped_bottom > 0)
            {
                sum += bot_val * init_clamped_bottom;
            }

            int32_t y = 0;
            for (; y < top_end; ++y)
            {
                dcol[static_cast<size_t>(y) * stride] = scalar_divisor.divide(sum);
                sum += static_cast<int32_t>(scol[static_cast<size_t>(std::min(h - 1, y + radius + 1)) * stride]) - top_val;
            }
            for (; y < bottom_start; ++y)
            {
                const size_t row_off = static_cast<size_t>(y) * stride;
                dcol[row_off] = scalar_divisor.divide(sum);
                sum += static_cast<int32_t>(scol[row_off + static_cast<size_t>(radius + 1) * stride])
                     - static_cast<int32_t>(scol[row_off - static_cast<size_t>(radius) * stride]);
            }
            for (; y < h; ++y)
            {
                dcol[static_cast<size_t>(y) * stride] = scalar_divisor.divide(sum);
                sum += bot_val - static_cast<int32_t>(scol[static_cast<size_t>(std::max(0, y - radius)) * stride]);
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
