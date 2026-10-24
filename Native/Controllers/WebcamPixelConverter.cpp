#include "../vnotch_common.h"
#include <immintrin.h>
#include <algorithm>
#include <cstdint>

namespace vnotch::controllers
{

class WebcamPixelConverter final
{
    static __forceinline void WriteYuvBt601(uint8_t* output, int32_t offset, int32_t y, int32_t u, int32_t v) noexcept
    {
        int32_t luminance = 298 * (y - 16);
        int32_t blue_diff = u - 128;
        int32_t red_diff = v - 128;
        output[offset] = static_cast<uint8_t>(std::clamp((luminance + 516 * blue_diff + 128) >> 8, 0, 255));
        output[offset + 1] = static_cast<uint8_t>(std::clamp((luminance - 100 * blue_diff - 208 * red_diff + 128) >> 8, 0, 255));
        output[offset + 2] = static_cast<uint8_t>(std::clamp((luminance + 409 * red_diff + 128) >> 8, 0, 255));
        output[offset + 3] = 255u;
    }

    // Same fixed-point BT.601 math as WriteYuvBt601, evaluated for 8 pixels held as int32 lanes.
    static __forceinline __m256i YuvToBgra8Lanes(__m256i y, __m256i u, __m256i v) noexcept
    {
        const __m256i rounding = _mm256_set1_epi32(128);
        const __m256i luminance = _mm256_mullo_epi32(_mm256_sub_epi32(y, _mm256_set1_epi32(16)), _mm256_set1_epi32(298));
        const __m256i blue_diff = _mm256_sub_epi32(u, _mm256_set1_epi32(128));
        const __m256i red_diff = _mm256_sub_epi32(v, _mm256_set1_epi32(128));

        __m256i b = _mm256_add_epi32(_mm256_add_epi32(luminance, _mm256_mullo_epi32(blue_diff, _mm256_set1_epi32(516))), rounding);
        __m256i g = _mm256_add_epi32(
            _mm256_sub_epi32(
                _mm256_sub_epi32(luminance, _mm256_mullo_epi32(blue_diff, _mm256_set1_epi32(100))),
                _mm256_mullo_epi32(red_diff, _mm256_set1_epi32(208))),
            rounding);
        __m256i r = _mm256_add_epi32(_mm256_add_epi32(luminance, _mm256_mullo_epi32(red_diff, _mm256_set1_epi32(409))), rounding);

        const __m256i zero = _mm256_setzero_si256();
        const __m256i max_value = _mm256_set1_epi32(255);
        b = _mm256_min_epi32(_mm256_max_epi32(_mm256_srai_epi32(b, 8), zero), max_value);
        g = _mm256_min_epi32(_mm256_max_epi32(_mm256_srai_epi32(g, 8), zero), max_value);
        r = _mm256_min_epi32(_mm256_max_epi32(_mm256_srai_epi32(r, 8), zero), max_value);

        return _mm256_or_si256(
            _mm256_or_si256(b, _mm256_slli_epi32(g, 8)),
            _mm256_or_si256(_mm256_slli_epi32(r, 16), _mm256_set1_epi32(static_cast<int32_t>(0xFF000000u))));
    }

    static __forceinline __m256i Premultiply16(__m256i channels) noexcept
    {
        // (c * a + 127) / 255 with the exact identity floor(x / 255) == (x + 1 + (x >> 8)) >> 8 for x < 65535.
        const __m256i alpha = _mm256_shufflehi_epi16(_mm256_shufflelo_epi16(channels, 0xFF), 0xFF);
        const __m256i t = _mm256_add_epi16(_mm256_mullo_epi16(channels, alpha), _mm256_set1_epi16(127));
        const __m256i q = _mm256_srli_epi16(
            _mm256_add_epi16(_mm256_add_epi16(t, _mm256_set1_epi16(1)), _mm256_srli_epi16(t, 8)), 8);
        return _mm256_blend_epi16(q, channels, 0x88);
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
        const __m256i swap_rb = _mm256_setr_epi8(
            2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
            2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
        const __m256i opaque_alpha = _mm256_set1_epi32(static_cast<int32_t>(0xFF000000u));
        const __m256i zero = _mm256_setzero_si256();
        for (; i + 32 <= length; i += 32)
        {
            __m256i block = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(pixels + i));
            if (is_rgba != 0)
            {
                block = _mm256_shuffle_epi8(block, swap_rb);
            }
            if (alpha_mode == 2)
            {
                block = _mm256_or_si256(block, opaque_alpha);
            }
            else if (alpha_mode == 1)
            {
                const __m256i lo = Premultiply16(_mm256_unpacklo_epi8(block, zero));
                const __m256i hi = Premultiply16(_mm256_unpackhi_epi8(block, zero));
                block = _mm256_packus_epi16(lo, hi);
            }
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(pixels + i), block);
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
        if (src == nullptr || dst == nullptr || pixel_count < 0)
        {
            return VN_ERR_INVALID_ARG;
        }

        int32_t i = 0;
        const __m256i expand = _mm256_setr_epi8(
            0, 0, 0, -1, 1, 1, 1, -1, 2, 2, 2, -1, 3, 3, 3, -1,
            0, 0, 0, -1, 1, 1, 1, -1, 2, 2, 2, -1, 3, 3, 3, -1);
        const __m256i opaque_alpha = _mm256_set1_epi32(static_cast<int32_t>(0xFF000000u));
        for (; i + 8 <= pixel_count; i += 8)
        {
            const __m128i gray = _mm_loadl_epi64(reinterpret_cast<const __m128i*>(src + i));
            const __m256i both_lanes = _mm256_set_m128i(_mm_srli_si128(gray, 4), gray);
            const __m256i bgra = _mm256_or_si256(_mm256_shuffle_epi8(both_lanes, expand), opaque_alpha);
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst + static_cast<size_t>(i) * 4u), bgra);
        }
        for (; i < pixel_count; ++i)
        {
            int32_t offset = i * 4;
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
        if (src == nullptr || dst == nullptr || width <= 0 || height <= 0)
        {
            return VN_ERR_INVALID_ARG;
        }
        const __m128i pick_u = _mm_setr_epi8(0, 0, 2, 2, 4, 4, 6, 6, -1, -1, -1, -1, -1, -1, -1, -1);
        const __m128i pick_v = _mm_setr_epi8(1, 1, 3, 3, 5, 5, 7, 7, -1, -1, -1, -1, -1, -1, -1, -1);
        int32_t pixels = width * height;
        for (int32_t y = 0; y < height; ++y)
        {
            int32_t y_row = y * width;
            int32_t uv_row = pixels + (y / 2) * width;
            int32_t x = 0;
            for (; x + 8 <= width; x += 8)
            {
                const __m128i luma = _mm_loadl_epi64(reinterpret_cast<const __m128i*>(src + y_row + x));
                const __m128i chroma = _mm_loadl_epi64(reinterpret_cast<const __m128i*>(src + uv_row + x));
                const __m256i bgra = YuvToBgra8Lanes(
                    _mm256_cvtepu8_epi32(luma),
                    _mm256_cvtepu8_epi32(_mm_shuffle_epi8(chroma, pick_u)),
                    _mm256_cvtepu8_epi32(_mm_shuffle_epi8(chroma, pick_v)));
                _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst + static_cast<size_t>(y_row + x) * 4u), bgra);
            }
            for (; x < width; x += 2)
            {
                int32_t idx = y_row + x;
                int32_t uv = uv_row + x;
                int32_t u = src[uv];
                int32_t v = src[uv + 1];
                WriteYuvBt601(dst, idx * 4, src[idx], u, v);
                if (x + 1 < width)
                {
                    WriteYuvBt601(dst, (idx + 1) * 4, src[idx + 1], u, v);
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
        if (src == nullptr || dst == nullptr || pixel_count < 0)
        {
            return VN_ERR_INVALID_ARG;
        }
        const __m128i pick_y = _mm_setr_epi8(0, 2, 4, 6, 8, 10, 12, 14, -1, -1, -1, -1, -1, -1, -1, -1);
        const __m128i pick_u = _mm_setr_epi8(1, 1, 5, 5, 9, 9, 13, 13, -1, -1, -1, -1, -1, -1, -1, -1);
        const __m128i pick_v = _mm_setr_epi8(3, 3, 7, 7, 11, 11, 15, 15, -1, -1, -1, -1, -1, -1, -1, -1);
        int32_t i = 0;
        for (; i + 8 <= pixel_count; i += 8)
        {
            const __m128i packed = _mm_loadu_si128(reinterpret_cast<const __m128i*>(src + static_cast<size_t>(i) * 2u));
            const __m256i bgra = YuvToBgra8Lanes(
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(packed, pick_y)),
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(packed, pick_u)),
                _mm256_cvtepu8_epi32(_mm_shuffle_epi8(packed, pick_v)));
            _mm256_storeu_si256(reinterpret_cast<__m256i*>(dst + static_cast<size_t>(i) * 4u), bgra);
        }
        for (; i < pixel_count; i += 2)
        {
            int32_t offset = i * 2;
            int32_t u = src[offset + 1];
            if (i + 1 < pixel_count)
            {
                int32_t v = src[offset + 3];
                WriteYuvBt601(dst, i * 4, src[offset], u, v);
                WriteYuvBt601(dst, (i + 1) * 4, src[offset + 2], u, v);
                continue;
            }
            WriteYuvBt601(dst, i * 4, src[offset], u, 128);
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
