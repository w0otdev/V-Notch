#include "../vnotch_common.h"
#include "../vnotch_cpu.h"
#include "../vnotch_simd_kernels.h"
#include <algorithm>
#include <cstdint>

namespace vnotch::controllers
{

class WebcamPixelConverter final
{
    // Frames are addressed with int32 pixel counts, so the BGRA byte count (count * 4) has to fit in int32 as well.
    static constexpr int32_t kMaxPixels = INT32_MAX / 4;

    static __forceinline void WriteYuvBt601(uint8_t* output, size_t offset, int32_t y, int32_t u, int32_t v) noexcept
    {
        int32_t luminance = 298 * (y - 16);
        int32_t blue_diff = u - 128;
        int32_t red_diff = v - 128;
        output[offset] = static_cast<uint8_t>(std::clamp((luminance + 516 * blue_diff + 128) >> 8, 0, 255));
        output[offset + 1] = static_cast<uint8_t>(std::clamp((luminance - 100 * blue_diff - 208 * red_diff + 128) >> 8, 0, 255));
        output[offset + 2] = static_cast<uint8_t>(std::clamp((luminance + 409 * red_diff + 128) >> 8, 0, 255));
        output[offset + 3] = 255u;
    }

public:
    static int32_t ConvertRgbInPlace(
        uint8_t* pixels,
        int32_t length,
        uint8_t is_rgba,
        int32_t alpha_mode) noexcept
    {
        if (pixels == nullptr || length < 0)
        {
            return VN_ERR_INVALID_ARG;
        }
        // BitmapAlphaMode: Premultiplied = 0, Straight = 1, Ignore = 2
        if (is_rgba == 0 && alpha_mode == 0)
        {
            return VN_OK;
        }

        int32_t i = 0;
        if (vnotch::cpu::SupportsX64V3())
        {
            i = vnotch::kernels::avx2::WebcamRgbInPlaceBulk(pixels, length, is_rgba, alpha_mode);
        }

        for (; i <= length - 4; i += 4)
        {
            if (is_rgba != 0)
            {
                uint8_t tmp = pixels[i];
                pixels[i] = pixels[i + 2];
                pixels[i + 2] = tmp;
            }
            if (alpha_mode == 2)
            {
                pixels[i + 3] = 255u;
            }
            else if (alpha_mode == 1)
            {
                int32_t alpha = pixels[i + 3];
                pixels[i] = static_cast<uint8_t>((pixels[i] * alpha + 127) / 255);
                pixels[i + 1] = static_cast<uint8_t>((pixels[i + 1] * alpha + 127) / 255);
                pixels[i + 2] = static_cast<uint8_t>((pixels[i + 2] * alpha + 127) / 255);
            }
        }
        return VN_OK;
    }

    static int32_t ConvertGray8ToBgra8(
        const uint8_t* src,
        int32_t pixel_count,
        uint8_t* dst) noexcept
    {
        if (src == nullptr || dst == nullptr || pixel_count < 0 || pixel_count > kMaxPixels)
        {
            return VN_ERR_INVALID_ARG;
        }

        int32_t i = 0;
        if (vnotch::cpu::SupportsX64V3())
        {
            i = vnotch::kernels::avx2::WebcamGray8Bulk(src, pixel_count, dst);
        }
        for (; i < pixel_count; ++i)
        {
            const size_t offset = static_cast<size_t>(i) * 4u;
            uint8_t v = src[i];
            dst[offset] = v;
            dst[offset + 1] = v;
            dst[offset + 2] = v;
            dst[offset + 3] = 255u;
        }
        return VN_OK;
    }

    static int32_t ConvertNv12ToBgra8(
        const uint8_t* src,
        int32_t width,
        int32_t height,
        uint8_t* dst) noexcept
    {
        if (src == nullptr || dst == nullptr || width <= 0 || height <= 0
            || (width & 1) != 0 || (height & 1) != 0
            || static_cast<int64_t>(width) * static_cast<int64_t>(height) > kMaxPixels)
        {
            return VN_ERR_INVALID_ARG;
        }
        const bool use_avx2 = vnotch::cpu::SupportsX64V3();
        const size_t pixels = static_cast<size_t>(width) * static_cast<size_t>(height);
        for (int32_t y = 0; y < height; ++y)
        {
            const size_t y_row = static_cast<size_t>(y) * static_cast<size_t>(width);
            const size_t uv_row = pixels + static_cast<size_t>(y / 2) * static_cast<size_t>(width);
            int32_t x = 0;
            if (use_avx2)
            {
                x = vnotch::kernels::avx2::WebcamNv12RowBulk(src + y_row, src + uv_row, width, dst + y_row * 4u);
            }
            for (; x < width; x += 2)
            {
                const size_t idx = y_row + static_cast<size_t>(x);
                const size_t uv = uv_row + static_cast<size_t>(x);
                int32_t u = src[uv];
                int32_t v = src[uv + 1];
                WriteYuvBt601(dst, idx * 4u, src[idx], u, v);
                if (x + 1 < width)
                {
                    WriteYuvBt601(dst, (idx + 1u) * 4u, src[idx + 1], u, v);
                }
            }
        }
        return VN_OK;
    }

    static int32_t ConvertYuy2ToBgra8(
        const uint8_t* src,
        int32_t pixel_count,
        uint8_t* dst) noexcept
    {
        if (src == nullptr || dst == nullptr || pixel_count < 0 || pixel_count > kMaxPixels)
        {
            return VN_ERR_INVALID_ARG;
        }
        int32_t i = 0;
        if (vnotch::cpu::SupportsX64V3())
        {
            i = vnotch::kernels::avx2::WebcamYuy2Bulk(src, pixel_count, dst);
        }
        for (; i < pixel_count; i += 2)
        {
            const size_t offset = static_cast<size_t>(i) * 2u;
            int32_t u = src[offset + 1];
            if (i + 1 < pixel_count)
            {
                int32_t v = src[offset + 3];
                WriteYuvBt601(dst, static_cast<size_t>(i) * 4u, src[offset], u, v);
                WriteYuvBt601(dst, (static_cast<size_t>(i) + 1u) * 4u, src[offset + 2], u, v);
                continue;
            }
            WriteYuvBt601(dst, static_cast<size_t>(i) * 4u, src[offset], u, 128);
        }
        return VN_OK;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t vn_webcam_convert_rgb_in_place(
    uint8_t* pixels,
    int32_t length,
    uint8_t is_rgba,
    int32_t alpha_mode) noexcept
{
    return vnotch::controllers::WebcamPixelConverter::ConvertRgbInPlace(pixels, length, is_rgba, alpha_mode);
}

VN_EXPORT int32_t vn_webcam_convert_gray8_to_bgra8(
    const uint8_t* src,
    int32_t pixel_count,
    uint8_t* dst) noexcept
{
    return vnotch::controllers::WebcamPixelConverter::ConvertGray8ToBgra8(src, pixel_count, dst);
}

VN_EXPORT int32_t vn_webcam_convert_nv12_to_bgra8(
    const uint8_t* src,
    int32_t width,
    int32_t height,
    uint8_t* dst) noexcept
{
    return vnotch::controllers::WebcamPixelConverter::ConvertNv12ToBgra8(src, width, height, dst);
}

VN_EXPORT int32_t vn_webcam_convert_yuy2_to_bgra8(
    const uint8_t* src,
    int32_t pixel_count,
    uint8_t* dst) noexcept
{
    return vnotch::controllers::WebcamPixelConverter::ConvertYuy2ToBgra8(src, pixel_count, dst);
}

} // extern "C"
