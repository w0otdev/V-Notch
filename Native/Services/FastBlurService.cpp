#include "vnotch_common.h"
#include "../vnotch_cpu.h"
#include "../vnotch_simd_kernels.h"
#include <algorithm>
#include <cmath>
#include <cstring>

namespace vnotch::services
{
    class FastBlurService final
    {
        static void DarkenScalar(uint8_t* pixels, int32_t length, int32_t start, float factor) noexcept
        {
            if (pixels == nullptr || length <= 0 || start < 0 || !std::isfinite(factor))
            {
                return;
            }
            factor = std::clamp(factor, 0.0f, 1.0f);
            const int64_t len = static_cast<int64_t>(length);
            for (int64_t i = static_cast<int64_t>(start); i + 2 < len; i += 4)
            {
                pixels[i] = static_cast<uint8_t>(pixels[i] * factor);
                pixels[i + 1] = static_cast<uint8_t>(pixels[i + 1] * factor);
                pixels[i + 2] = static_cast<uint8_t>(pixels[i + 2] * factor);
            }
        }

    public:
        // 2 * radius + 1 must stay far below 2^24 so the fixed-point reciprocal and the running sums cannot overflow.
        static constexpr int32_t kMaxRadius = 256;

        static constexpr bool IsValidDimensions(int32_t w, int32_t h, int32_t radius) noexcept
        {
            return w > 0 && h > 0 &&
                   radius >= 0 && radius <= kMaxRadius &&
                   static_cast<int64_t>(w) * static_cast<int64_t>(h) <= (INT32_MAX / 4);
        }

        static constexpr uint32_t GetWindowReciprocal(int32_t window) noexcept
        {
            if (window <= 0)
            {
                return 0u;
            }
            uint32_t w = static_cast<uint32_t>(window);
            return ((1u << 24) + w - 1u) / w;
        }

        static void DarkenPixels(uint8_t* pixels, int32_t length, float factor) noexcept
        {
            if (pixels == nullptr || length <= 0 || !std::isfinite(factor))
            {
                return;
            }
            factor = std::clamp(factor, 0.0f, 1.0f);
            int32_t i = 0;
            if (vnotch::cpu::SupportsX64V3())
            {
                i = vnotch::kernels::avx2::DarkenPixelsBulk(pixels, length, factor);
            }
            DarkenScalar(pixels, length, i, factor);
        }

        static void BoxBlurHorizontal(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
        {
            if (!source || !target || !IsValidDimensions(w, h, radius))
            {
                return;
            }
            if (vnotch::cpu::SupportsX64V3())
            {
                vnotch::kernels::avx2::BoxBlurHorizontal(source, target, w, h, radius);
                return;
            }

            const int32_t window = 2 * radius + 1;
            const uint32_t reciprocal = GetWindowReciprocal(window);
            const uint32_t first_edge_count = static_cast<uint32_t>(radius) + 1u;
            const int64_t last_x = static_cast<int64_t>(w) - 1;

            for (int32_t y = 0; y < h; ++y)
            {
                const size_t row = static_cast<size_t>(y) * static_cast<size_t>(w) * 4u;
                const uint8_t* src = source + row;
                uint8_t* dst = target + row;

                uint32_t sum[4];
                for (int32_t c = 0; c < 4; ++c)
                {
                    sum[c] = src[c] * first_edge_count;
                }
                for (int32_t dx = 1; dx <= radius; ++dx)
                {
                    const int64_t nx = dx < last_x ? dx : last_x;
                    const uint8_t* px = src + static_cast<size_t>(nx) * 4u;
                    for (int32_t c = 0; c < 4; ++c)
                    {
                        sum[c] += px[c];
                    }
                }

                for (int32_t x = 0; x < w; ++x)
                {
                    uint8_t* out = dst + static_cast<size_t>(x) * 4u;
                    for (int32_t c = 0; c < 4; ++c)
                    {
                        out[c] = static_cast<uint8_t>((sum[c] * reciprocal) >> 24);
                    }

                    const int64_t out_x = std::clamp<int64_t>(static_cast<int64_t>(x) - radius, 0, last_x);
                    const int64_t in_x = std::clamp<int64_t>(static_cast<int64_t>(x) + 1 + radius, 0, last_x);
                    const uint8_t* in_px = src + static_cast<size_t>(in_x) * 4u;
                    const uint8_t* out_px = src + static_cast<size_t>(out_x) * 4u;
                    for (int32_t c = 0; c < 4; ++c)
                    {
                        sum[c] += static_cast<uint32_t>(in_px[c]) - static_cast<uint32_t>(out_px[c]);
                    }
                }
            }
        }

        static void BoxBlurVertical(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
        {
            if (!source || !target || !IsValidDimensions(w, h, radius))
            {
                return;
            }
            if (vnotch::cpu::SupportsX64V3())
            {
                vnotch::kernels::avx2::BoxBlurVertical(source, target, w, h, radius);
                return;
            }

            const int32_t window = 2 * radius + 1;
            const uint32_t reciprocal = GetWindowReciprocal(window);
            const size_t row_stride = static_cast<size_t>(w) * 4u;
            const int32_t first_edge_count = radius + 1;

            // The row is processed in fixed-size column strips so the running sums always live on the stack.
            // This avoids a heap allocation (and a possible bad_alloc) inside the noexcept export.
            constexpr size_t kStripWidth = 1024;
            int32_t sums[kStripWidth];

            for (size_t strip = 0; strip < row_stride; strip += kStripWidth)
            {
                const size_t count = std::min(kStripWidth, row_stride - strip);
                const uint8_t* src = source + strip;
                uint8_t* dst = target + strip;

                for (size_t c = 0; c < count; ++c)
                {
                    sums[c] = src[c] * first_edge_count;
                }

                for (int32_t dy = 1; dy <= radius; ++dy)
                {
                    const uint8_t* row = src + static_cast<size_t>(std::clamp(dy, 0, h - 1)) * row_stride;
                    for (size_t c = 0; c < count; ++c)
                    {
                        sums[c] += row[c];
                    }
                }

                for (int32_t y = 0; y < h; ++y)
                {
                    const size_t row = static_cast<size_t>(y) * row_stride;
                    const uint8_t* out_row = src + static_cast<size_t>(std::clamp(y - radius, 0, h - 1)) * row_stride;
                    const uint8_t* in_row = src + static_cast<size_t>(std::clamp(y + radius + 1, 0, h - 1)) * row_stride;
                    uint8_t* dst_row = dst + row;
                    for (size_t c = 0; c < count; ++c)
                    {
                        dst_row[c] = static_cast<uint8_t>((static_cast<uint32_t>(sums[c]) * reciprocal) >> 24);
                        sums[c] += in_row[c] - out_row[c];
                    }
                }
            }
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_fast_blur_get_window_reciprocal(int32_t window, uint32_t* out_reciprocal) noexcept
{
    if (out_reciprocal == nullptr || window <= 0 || window > 2 * vnotch::services::FastBlurService::kMaxRadius + 1)
        return VN_ERR_INVALID_ARG;
    *out_reciprocal = vnotch::services::FastBlurService::GetWindowReciprocal(window);
    return VN_OK;
}

VN_EXPORT int32_t vn_fast_blur_darken_pixels(uint8_t* pixels, int32_t length, float factor) noexcept
{
    if (length < 0 || (length > 0 && pixels == nullptr) || !std::isfinite(factor))
        return VN_ERR_INVALID_ARG;
    vnotch::services::FastBlurService::DarkenPixels(pixels, length, factor);
    return VN_OK;
}

VN_EXPORT int32_t vn_fast_blur_box_horizontal(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
{
    if (!source || !target || !vnotch::services::FastBlurService::IsValidDimensions(w, h, radius))
        return VN_ERR_INVALID_ARG;
    vnotch::services::FastBlurService::BoxBlurHorizontal(source, target, w, h, radius);
    return VN_OK;
}

VN_EXPORT int32_t vn_fast_blur_box_vertical(const uint8_t* source, uint8_t* target, int32_t w, int32_t h, int32_t radius) noexcept
{
    if (!source || !target || !vnotch::services::FastBlurService::IsValidDimensions(w, h, radius))
        return VN_ERR_INVALID_ARG;
    vnotch::services::FastBlurService::BoxBlurVertical(source, target, w, h, radius);
    return VN_OK;
}
