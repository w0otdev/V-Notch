#include "vnotch_common.h"

namespace vnotch::services
{
    class ColorExtractionService final
    {
    public:
        struct ColorBucket
        {
            int32_t count;
            int32_t sum_r;
            int32_t sum_g;
            int32_t sum_b;
        };

        static void EnhanceSaturation(uint8_t& io_r, uint8_t& io_g, uint8_t& io_b, double factor) noexcept
        {
            double r = io_r / 255.0;
            double g = io_g / 255.0;
            double b = io_b / 255.0;

            double max_v = std::max(r, std::max(g, b));
            double min_v = std::min(r, std::min(g, b));
            double delta = max_v - min_v;

            double h = 0.0, s = 0.0, v = max_v;
            if (delta > 0.0)
            {
                s = delta / max_v;
                if (std::abs(r - max_v) < 0.0001)
                    h = (g - b) / delta + (g < b ? 6.0 : 0.0);
                else if (std::abs(g - max_v) < 0.0001)
                    h = (b - r) / delta + 2.0;
                else
                    h = (r - g) / delta + 4.0;
                h /= 6.0;
            }

            s = std::min(1.0, s * factor);
            double c = v * s;
            double x = c * (1.0 - std::abs(std::fmod(h * 6.0, 2.0) - 1.0));
            double m = v - c;

            double r_prime = 0.0, g_prime = 0.0, b_prime = 0.0;
            if (h < 1.0 / 6.0) { r_prime = c; g_prime = x; b_prime = 0.0; }
            else if (h < 2.0 / 6.0) { r_prime = x; g_prime = c; b_prime = 0.0; }
            else if (h < 3.0 / 6.0) { r_prime = 0.0; g_prime = c; b_prime = x; }
            else if (h < 4.0 / 6.0) { r_prime = 0.0; g_prime = x; b_prime = c; }
            else if (h < 5.0 / 6.0) { r_prime = x; g_prime = 0.0; b_prime = c; }
            else { r_prime = c; g_prime = 0.0; b_prime = x; }

            io_r = static_cast<uint8_t>(std::nearbyint((r_prime + m) * 255.0));
            io_g = static_cast<uint8_t>(std::nearbyint((g_prime + m) * 255.0));
            io_b = static_cast<uint8_t>(std::nearbyint((b_prime + m) * 255.0));
        }

        static void EnsureMinimumBrightness(uint8_t& io_r, uint8_t& io_g, uint8_t& io_b, int32_t min_brightness) noexcept
        {
            int32_t current = (static_cast<int32_t>(io_r) + static_cast<int32_t>(io_g) + static_cast<int32_t>(io_b)) / 3;
            if (current >= min_brightness)
            {
                return;
            }
            double scale = static_cast<double>(min_brightness) / static_cast<double>(std::max(current, 1));
            io_r = static_cast<uint8_t>(std::min(255.0, io_r * scale));
            io_g = static_cast<uint8_t>(std::min(255.0, io_g * scale));
            io_b = static_cast<uint8_t>(std::min(255.0, io_b * scale));
        }

        static int32_t ExtractSampledColor(
            const uint8_t* pixels,
            int32_t pixel_len,
            int32_t width,
            int32_t height,
            const double* coords,
            int32_t coords_len,
            uint8_t* out_r,
            uint8_t* out_g,
            uint8_t* out_b) noexcept
        {
            if (!pixels || !coords || !out_r || !out_g || !out_b || width <= 0 || height <= 0 || pixel_len <= 0 || coords_len < 0)
            {
                return VN_ERR_INVALID_ARG;
            }
            ColorBucket buckets[216]{};
            int32_t order[216]{};
            int32_t used = 0;

            for (int32_t i = 0; i + 1 < coords_len; i += 2)
            {
                double cx = coords[i];
                double cy = coords[i + 1];
                if (!std::isfinite(cx) || !std::isfinite(cy) || cx < 0.0 || cx >= 1.0 || cy < 0.0 || cy >= 1.0)
                {
                    continue;
                }
                int64_t x = static_cast<int64_t>(cx * width);
                int64_t y = static_cast<int64_t>(cy * height);
                int64_t index = (y * static_cast<int64_t>(width) + x) * 4LL;
                if (index < 0 || index + 3 >= static_cast<int64_t>(pixel_len))
                {
                    continue;
                }
                uint8_t b = pixels[index];
                uint8_t g = pixels[index + 1];
                uint8_t r = pixels[index + 2];
                int32_t brightness = (static_cast<int32_t>(r) + static_cast<int32_t>(g) + static_cast<int32_t>(b)) / 3;
                if (brightness < 60 || brightness > 245 || pixels[index + 3] < 100)
                {
                    continue;
                }

                int32_t key = ((r / 50) * 6 + (g / 50)) * 6 + (b / 50);
                ColorBucket& bucket = buckets[key];
                if (bucket.count == 0)
                {
                    order[used++] = key;
                }
                ++bucket.count;
                bucket.sum_r += r;
                bucket.sum_g += g;
                bucket.sum_b += b;
            }

            ColorBucket best{};
            for (int32_t i = 0; i < used; ++i)
            {
                const ColorBucket& bucket = buckets[order[i]];
                if (bucket.count > best.count)
                {
                    best = bucket;
                }
            }
            if (best.count == 0)
            {
                *out_r = 255u;
                *out_g = 255u;
                *out_b = 255u;
                return VN_OK;
            }

            uint8_t r = static_cast<uint8_t>(best.sum_r / best.count);
            uint8_t g = static_cast<uint8_t>(best.sum_g / best.count);
            uint8_t b = static_cast<uint8_t>(best.sum_b / best.count);
            EnhanceSaturation(r, g, b, 1.3);
            EnsureMinimumBrightness(r, g, b, 100);
            *out_r = r;
            *out_g = g;
            *out_b = b;
            return VN_OK;
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_color_extract_sampled(
    const uint8_t* pixels,
    int32_t pixel_len,
    int32_t width,
    int32_t height,
    const double* coords,
    int32_t coords_len,
    uint8_t* out_r,
    uint8_t* out_g,
    uint8_t* out_b) noexcept
{
    return vnotch::services::ColorExtractionService::ExtractSampledColor(
        pixels, pixel_len, width, height, coords, coords_len, out_r, out_g, out_b);
}
