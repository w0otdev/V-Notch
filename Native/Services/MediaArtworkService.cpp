#include "vnotch_common.h"
#include <algorithm>
#include <cmath>

namespace vnotch::services
{

class MediaArtworkService final
{
    static constexpr int32_t kBlackThreshold = 25;

    static bool IsRowDark(const uint8_t* pixels, int64_t row_offset, int32_t start_x, int32_t end_x) noexcept
    {
        int32_t total = end_x - start_x;
        if (total <= 0)
        {
            return false;
        }
        int32_t dark_pixels = 0;
        for (int32_t x = start_x; x < end_x; ++x)
        {
            int64_t i = row_offset + static_cast<int64_t>(x) * 4LL;
            if (pixels[i] < kBlackThreshold && pixels[i + 1] < kBlackThreshold && pixels[i + 2] < kBlackThreshold)
            {
                ++dark_pixels;
            }
        }
        return static_cast<double>(dark_pixels) / static_cast<double>(total) > 0.85;
    }

    static bool IsColumnDark(const uint8_t* pixels, int32_t col_x, int32_t stride, int32_t start_y, int32_t end_y) noexcept
    {
        int32_t total = end_y - start_y;
        if (total <= 0)
        {
            return false;
        }
        int32_t dark_pixels = 0;
        for (int32_t y = start_y; y < end_y; ++y)
        {
            int64_t i = static_cast<int64_t>(y) * stride + static_cast<int64_t>(col_x) * 4LL;
            if (pixels[i] < kBlackThreshold && pixels[i + 1] < kBlackThreshold && pixels[i + 2] < kBlackThreshold)
            {
                ++dark_pixels;
            }
        }
        return static_cast<double>(dark_pixels) / static_cast<double>(total) > 0.85;
    }

public:
    static int32_t DetectContentBoundsFromPixels(
        const uint8_t* pixels,
        int32_t pixels_len,
        int32_t stride,
        int32_t sw,
        int32_t sh,
        int32_t width,
        int32_t height,
        double scale_x,
        double scale_y,
        int32_t* out_x,
        int32_t* out_y,
        int32_t* out_w,
        int32_t* out_h) noexcept
    {
        if (!pixels || !out_x || !out_y || !out_w || !out_h ||
            sw < 4 || sh < 4 || width <= 0 || height <= 0 ||
            stride <= 0 || static_cast<int64_t>(stride) < static_cast<int64_t>(sw) * 4LL ||
            static_cast<int64_t>(pixels_len) < static_cast<int64_t>(sh) * static_cast<int64_t>(stride) ||
            !std::isfinite(scale_x) || scale_x <= 0.0 ||
            !std::isfinite(scale_y) || scale_y <= 0.0)
        {
            return VN_ERR_INVALID_ARG;
        }

        int32_t top_bar = 0;
        for (int32_t y = 0; y < sh / 3; ++y)
        {
            if (IsRowDark(pixels, static_cast<int64_t>(y) * stride, sw / 4, sw * 3 / 4))
                top_bar = y + 1;
            else
                break;
        }

        int32_t bottom_bar = 0;
        for (int32_t y = sh - 1; y >= sh * 2 / 3; --y)
        {
            if (IsRowDark(pixels, static_cast<int64_t>(y) * stride, sw / 4, sw * 3 / 4))
                ++bottom_bar;
            else
                break;
        }

        int32_t left_bar = 0;
        for (int32_t x = 0; x < sw / 3; ++x)
        {
            if (IsColumnDark(pixels, x, stride, sh / 4, sh * 3 / 4))
                left_bar = x + 1;
            else
                break;
        }

        int32_t right_bar = 0;
        for (int32_t x = sw - 1; x >= sw * 2 / 3; --x)
        {
            if (IsColumnDark(pixels, x, stride, sh / 4, sh * 3 / 4))
                ++right_bar;
            else
                break;
        }

        const double max_w = static_cast<double>(width);
        const double max_h = static_cast<double>(height);
        int32_t content_x = static_cast<int32_t>(std::clamp(left_bar / scale_x, 0.0, max_w));
        int32_t content_y = static_cast<int32_t>(std::clamp(top_bar / scale_y, 0.0, max_h));
        int32_t right_cut = static_cast<int32_t>(std::clamp(right_bar / scale_x, 0.0, max_w));
        int32_t bottom_cut = static_cast<int32_t>(std::clamp(bottom_bar / scale_y, 0.0, max_h));
        int32_t content_w = width - content_x - right_cut;
        int32_t content_h = height - content_y - bottom_cut;

        if (content_w < width / 2 || content_h < height / 2)
        {
            *out_x = 0;
            *out_y = 0;
            *out_w = width;
            *out_h = height;
        }
        else
        {
            *out_x = content_x;
            *out_y = content_y;
            *out_w = content_w;
            *out_h = content_h;
        }
        return VN_OK;
    }

    static void ComputeSquareCropMetrics(
        int32_t width,
        int32_t height,
        double& out_aspect,
        int32_t& out_square_size,
        int32_t& out_center_x,
        int32_t& out_center_y) noexcept
    {
        out_aspect = static_cast<double>(width) / static_cast<double>(height);
        double zoom = (out_aspect >= 0.9 && out_aspect <= 1.1) ? 1.0 : 0.97;
        out_square_size = static_cast<int32_t>(std::min(width, height) * zoom);
        out_center_x = (width - out_square_size) / 2;
        out_center_y = (height - out_square_size) / 2;
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_media_artwork_detect_content_bounds(
    const uint8_t* pixels,
    int32_t pixels_len,
    int32_t stride,
    int32_t sw,
    int32_t sh,
    int32_t width,
    int32_t height,
    double scale_x,
    double scale_y,
    int32_t* out_x,
    int32_t* out_y,
    int32_t* out_w,
    int32_t* out_h) noexcept
{
    return vnotch::services::MediaArtworkService::DetectContentBoundsFromPixels(
        pixels, pixels_len, stride, sw, sh, width, height, scale_x, scale_y, out_x, out_y, out_w, out_h);
}

VN_EXPORT int32_t vn_media_artwork_compute_square_crop_metrics(
    int32_t width,
    int32_t height,
    double* out_aspect,
    int32_t* out_square_size,
    int32_t* out_center_x,
    int32_t* out_center_y) noexcept
{
    if (!out_aspect || !out_square_size || !out_center_x || !out_center_y || width <= 0 || height <= 0)
    {
        return VN_ERR_INVALID_ARG;
    }
    vnotch::services::MediaArtworkService::ComputeSquareCropMetrics(
        width, height, *out_aspect, *out_square_size, *out_center_x, *out_center_y);
    return VN_OK;
}
