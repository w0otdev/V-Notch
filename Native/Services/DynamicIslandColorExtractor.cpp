#include "vnotch_common.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <span>
#include <vector>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnPaletteResult {
    uint8_t primary_r;
    uint8_t primary_g;
    uint8_t primary_b;
    uint8_t primary_a;
    uint8_t secondary_r;
    uint8_t secondary_g;
    uint8_t secondary_b;
    uint8_t secondary_a;
    uint8_t accent_r;
    uint8_t accent_g;
    uint8_t accent_b;
    uint8_t accent_a;
    uint8_t is_monotone;
    uint8_t is_flat_bg;
    uint8_t text_r;
    uint8_t text_g;
    uint8_t text_b;
    uint8_t text_a;
};
#pragma pack(pop)

class DynamicIslandColorExtractor final {
public:
    static bool GetClampedCropRect(
        double left, double top, double right, double bottom, uint8_t is_empty,
        int32_t pixel_width, int32_t pixel_height,
        int32_t* out_x, int32_t* out_y, int32_t* out_w, int32_t* out_h) noexcept
    {
        if (pixel_width <= 0 || pixel_height <= 0 || is_empty != 0 ||
            !std::isfinite(left) || !std::isfinite(top) ||
            !std::isfinite(right) || !std::isfinite(bottom)) {
            return false;
        }

        double i_left = std::max(left, 0.0);
        double i_top = std::max(top, 0.0);
        double i_right = std::min(right, static_cast<double>(pixel_width));
        double i_bottom = std::min(bottom, static_cast<double>(pixel_height));
        if (i_right <= i_left || i_bottom <= i_top) {
            return false;
        }

        int32_t cl_left = static_cast<int32_t>(std::clamp(std::floor(i_left), 0.0, static_cast<double>(pixel_width - 1)));
        int32_t cl_top = static_cast<int32_t>(std::clamp(std::floor(i_top), 0.0, static_cast<double>(pixel_height - 1)));
        int32_t cl_right = static_cast<int32_t>(std::clamp(std::ceil(i_right), static_cast<double>(cl_left + 1), static_cast<double>(pixel_width)));
        int32_t cl_bottom = static_cast<int32_t>(std::clamp(std::ceil(i_bottom), static_cast<double>(cl_top + 1), static_cast<double>(pixel_height)));
        *out_x = cl_left;
        *out_y = cl_top;
        *out_w = cl_right - cl_left;
        *out_h = cl_bottom - cl_top;
        return true;
    }

    static void EnsureBrightColor(uint8_t r, uint8_t g, uint8_t b, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept {
        double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        if (luminance < 0.5) {
            constexpr double factor = 0.7;
            *out_r = static_cast<uint8_t>(r + (255 - r) * factor);
            *out_g = static_cast<uint8_t>(g + (255 - g) * factor);
            *out_b = static_cast<uint8_t>(b + (255 - b) * factor);
            return;
        }
        *out_r = r;
        *out_g = g;
        *out_b = b;
    }

    static void GetVibrantColor(uint8_t cr, uint8_t cg, uint8_t cb, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept {
        double r = cr / 255.0, g = cg / 255.0, b = cb / 255.0;
        double max_c = std::max(r, std::max(g, b));
        double min_c = std::min(r, std::min(g, b));
        double h = 0.0, s = 0.0, l = (max_c + min_c) / 2.0;
        double d = max_c - min_c;
        if (d > 0.0) {
            s = l > 0.5 ? d / (2.0 - max_c - min_c) : d / (max_c + min_c);
            if (std::abs(max_c - r) < 0.0001) h = ((g - b) / d + (g < b ? 6.0 : 0.0)) / 6.0;
            else if (std::abs(max_c - g) < 0.0001) h = ((b - r) / d + 2.0) / 6.0;
            else h = ((r - g) / d + 4.0) / 6.0;
            s = std::min(s, 0.90);
        }

        if (s < 0.16) {
            uint8_t gray = static_cast<uint8_t>(std::clamp(0.2126 * cr + 0.7152 * cg + 0.0722 * cb, 0.0, 255.0));
            uint8_t lifted = static_cast<uint8_t>(std::clamp(gray + (255 - gray) * 0.18, 0.0, 255.0));
            *out_r = lifted;
            *out_g = lifted;
            *out_b = lifted;
            return;
        }

        l = std::max(l, 0.65);
        l = std::min(l, 0.85);
        HslToColor(h, s, l, out_r, out_g, out_b);
    }

    static double GetAdaptiveBlurOpacity(double luminance, double brightness_boost) noexcept {
        if (!std::isfinite(luminance)) luminance = 0.0;
        if (!std::isfinite(brightness_boost)) brightness_boost = 1.0;
        brightness_boost = std::clamp(brightness_boost, 0.5, 2.5);
        double base_opacity;
        if (luminance <= 0.72) {
            base_opacity = 0.55 + (brightness_boost - 0.5) * (0.45 / 2.0);
        } else {
            double t = std::clamp((luminance - 0.72) / 0.28, 0.0, 1.0);
            double full_base = 0.55 + (brightness_boost - 0.5) * (0.45 / 2.0);
            base_opacity = full_base - t * 0.10;
        }
        return std::clamp(base_opacity, 0.0, 1.0);
    }

    static double GetAdaptiveBlurImageOpacity(double luminance) noexcept {
        if (!std::isfinite(luminance)) luminance = 0.0;
        if (luminance <= 0.70) return 0.90;
        double t = std::clamp((luminance - 0.70) / 0.30, 0.0, 1.0);
        return 0.90 - t * 0.15;
    }

    static double ComputeBrightnessDim(std::span<const uint8_t> pixels, int32_t pixel_count) noexcept {
        int32_t buf_len = static_cast<int32_t>(pixels.size());
        const int32_t count = std::min(pixel_count, buf_len / 4);
        if (count <= 0) return 0.0;

        double total_luminance = 0.0;
        int32_t bright_pixel_count = 0;
        const int32_t limit = count * 4;

        for (int32_t i = 0; i + 3 < limit; i += 4) {
            double r = pixels[i + 2] / 255.0;
            double g = pixels[i + 1] / 255.0;
            double b = pixels[i] / 255.0;

            double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            total_luminance += lum;

            if (lum > 0.75) bright_pixel_count++;
        }

        double avg_luminance = total_luminance / count;
        double bright_ratio = static_cast<double>(bright_pixel_count) / count;

        double lum_contrib = avg_luminance > 0.55
            ? std::clamp((avg_luminance - 0.55) / 0.45, 0.0, 1.0) * 0.45
            : 0.0;

        double ratio_contrib = bright_ratio > 0.40
            ? std::clamp((bright_ratio - 0.40) / 0.50, 0.0, 1.0) * 0.40
            : 0.0;

        double combined = std::max(lum_contrib, ratio_contrib);
        if (avg_luminance > 0.55 && bright_ratio > 0.40) {
            combined += std::min(lum_contrib, ratio_contrib) * 0.3;
        }

        return std::clamp(combined, 0.0, 0.65);
    }

    static void ExtractAdvancedPalette(const uint8_t* pixels, int32_t width, int32_t height, int32_t stride, VnPaletteResult* out_res) noexcept {
        *out_res = VnPaletteResult{30, 30, 30, 255, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 255, 255, 255, 255};
        if (!pixels || width <= 4 || height <= 4 || width > 16384 || height > 16384 || static_cast<int64_t>(stride) < static_cast<int64_t>(width) * 4LL) {
            return;
        }

        constexpr int32_t NUM_BUCKETS = 36;
        std::array<float, NUM_BUCKETS> bucket_sat_sum{};
        std::array<float, NUM_BUCKETS> bucket_val_sum{};
        std::array<float, NUM_BUCKETS> bucket_weight{};
        std::array<int32_t, NUM_BUCKETS> bucket_count{};
        std::array<float, NUM_BUCKETS> bucket_peak_s{};
        std::array<float, NUM_BUCKETS> bucket_peak_h{};
        std::array<float, NUM_BUCKETS> bucket_peak_v{};

        float center_x = width / 2.0f;
        float center_y = height / 2.0f;
        int32_t total_color_pixels = 0;
        int32_t total_pixels = 0;

        for (int32_t y = 0; y < height; y++) {
            for (int32_t x = 0; x < width; x++) {
                int64_t i = static_cast<int64_t>(y) * stride + static_cast<int64_t>(x) * 4LL;
                uint8_t a = pixels[i + 3];
                if (a < 80) continue;

                float rf = pixels[i + 2] / 255.0f;
                float gf = pixels[i + 1] / 255.0f;
                float bf = pixels[i] / 255.0f;

                float h = 0.0f, s = 0.0f, v = 0.0f;
                RgbToHsv(rf, gf, bf, &h, &s, &v);
                total_pixels++;

                if (v < 0.06f) continue;
                if (s < 0.12f) continue;

                total_color_pixels++;

                int32_t bucket = static_cast<int32_t>(h * NUM_BUCKETS) % NUM_BUCKETS;
                if (bucket < 0) bucket += NUM_BUCKETS;

                float dx = (x - center_x) / center_x;
                float dy = (y - center_y) / center_y;
                float zone_weight = 1.0f + std::max(0.0f, 1.0f - std::sqrt(dx * dx + dy * dy)) * 0.5f;

                bucket_sat_sum[bucket] += s * zone_weight;
                bucket_val_sum[bucket] += v * zone_weight;
                bucket_weight[bucket] += zone_weight;
                bucket_count[bucket]++;

                float vibrancy = s * std::max(v, 0.3f);
                if (vibrancy > bucket_peak_s[bucket]) {
                    bucket_peak_s[bucket] = vibrancy;
                    bucket_peak_h[bucket] = h;
                    bucket_peak_v[bucket] = std::max(v, 0.4f);
                }
            }
        }

        bool is_monotone = total_pixels > 0 && (static_cast<float>(total_color_pixels) / total_pixels) < 0.08f;
        if (is_monotone) {
            return;
        }

        float best_score = -1.0f, second_score = -1.0f;
        int32_t best_bucket = -1, second_bucket = -1;
        float best_non_indigo_score = -1.0f;
        int32_t best_non_indigo_bucket = -1;
        const int32_t min_alt_pixels = std::max(1, total_color_pixels / 20);

        for (int32_t i = 0; i < NUM_BUCKETS; i++) {
            if (bucket_count[i] == 0) continue;

            float area = static_cast<float>(bucket_count[i]) / std::max(total_color_pixels, 1);
            float avg_sat = bucket_sat_sum[i] / bucket_weight[i];
            float avg_val = bucket_val_sum[i] / bucket_weight[i];

            float score = std::pow(area, 0.3f) * avg_sat * avg_val;
            if (IsHarshBlueIndigoHue(bucket_peak_h[i])) {
                score *= 0.15f;
            } else if (bucket_count[i] >= min_alt_pixels && score > best_non_indigo_score) {
                best_non_indigo_score = score;
                best_non_indigo_bucket = i;
            }

            if (score > best_score) {
                if (best_bucket >= 0) {
                    int32_t diff = std::abs(i - best_bucket);
                    int32_t hue_dist = std::min(diff, NUM_BUCKETS - diff);
                    if (hue_dist >= 4 && best_score > second_score) {
                        second_score = best_score;
                        second_bucket = best_bucket;
                    }
                }
                best_score = score;
                best_bucket = i;
            } else if (score > second_score) {
                int32_t diff = std::abs(i - best_bucket);
                int32_t hue_dist = std::min(diff, NUM_BUCKETS - diff);
                if (hue_dist >= 4) {
                    second_score = score;
                    second_bucket = i;
                }
            }
        }

        if (best_bucket >= 0 && IsHarshBlueIndigoHue(bucket_peak_h[best_bucket]) && best_non_indigo_bucket >= 0) {
            if (second_bucket < 0 || second_bucket == best_non_indigo_bucket) {
                second_bucket = best_bucket;
                second_score = best_score;
            }
            best_bucket = best_non_indigo_bucket;
            best_score = best_non_indigo_score;
        }

        uint8_t pr = 30, pg = 30, pb = 30, pa = 255;
        if (best_bucket >= 0) {
            float p_h = bucket_peak_h[best_bucket];
            float p_s = std::min(bucket_peak_s[best_bucket] / std::max(bucket_peak_v[best_bucket], 0.3f), 1.0f);
            float p_v = bucket_peak_v[best_bucket];
            p_v = std::max(p_v, 0.45f);
            p_s = std::max(p_s, 0.50f);
            HsvToColor(p_h, p_s, p_v, &pr, &pg, &pb);
        }

        uint8_t sr = 0, sg = 0, sb = 0, sa = 0;
        if (second_bucket >= 0) {
            float p_h = bucket_peak_h[second_bucket];
            float p_s = std::min(bucket_peak_s[second_bucket] / std::max(bucket_peak_v[second_bucket], 0.3f), 1.0f);
            float p_v = bucket_peak_v[second_bucket];
            p_v = std::max(p_v, 0.45f);
            p_s = std::max(p_s, 0.50f);
            HsvToColor(p_h, p_s, p_v, &sr, &sg, &sb);
            sa = 255;
        }

        uint8_t ar = 0, ag = 0, ab = 0;
        EnsureTextOnDarkBackground(pr, pg, pb, 0, 0, 0, 4.5, &ar, &ag, &ab);
        double ah = 0.0, as = 0.0, al = 0.0;
        ToHsl(ar, ag, ab, &ah, &as, &al);
        if (IsHarshBlueIndigoHue(ah) || (best_bucket >= 0 && IsHarshBlueIndigoHue(bucket_peak_h[best_bucket]))) {
            if (sa != 0 && second_bucket >= 0 && !IsHarshBlueIndigoHue(bucket_peak_h[second_bucket])) {
                EnsureTextOnDarkBackground(sr, sg, sb, 0, 0, 0, 4.5, &ar, &ag, &ab);
            } else {
                HslToColor(0.555, std::min(as * 0.35, 0.28), std::clamp(std::max(al, 0.80), 0.78, 0.86), &ar, &ag, &ab);
            }
        }

        bool is_flat_bg = best_score > 0.0f && second_score > 0.0f &&
                          (best_score / (best_score + second_score)) > 0.85f;

        double primary_lum = GetRelativeLuminance(pr, pg, pb);
        uint8_t tr = primary_lum > 0.4 ? 0 : 255;

        out_res->primary_r = pr;
        out_res->primary_g = pg;
        out_res->primary_b = pb;
        out_res->primary_a = pa;
        out_res->secondary_r = sr;
        out_res->secondary_g = sg;
        out_res->secondary_b = sb;
        out_res->secondary_a = sa;
        out_res->accent_r = ar;
        out_res->accent_g = ag;
        out_res->accent_b = ab;
        out_res->accent_a = 255;
        out_res->is_monotone = 0;
        out_res->is_flat_bg = is_flat_bg ? 1 : 0;
        out_res->text_r = tr;
        out_res->text_g = tr;
        out_res->text_b = tr;
        out_res->text_a = 255;
    }

    static void ToHsl(uint8_t cr, uint8_t cg, uint8_t cb, double* out_h, double* out_s, double* out_l) noexcept {
        double r = cr / 255.0, g = cg / 255.0, b = cb / 255.0;
        double max_c = std::max(r, std::max(g, b));
        double min_c = std::min(r, std::min(g, b));
        double h = 0.0, s = 0.0, l = (max_c + min_c) / 2.0;
        double d = max_c - min_c;
        if (d > 0.0001) {
            s = l > 0.5 ? d / (2.0 - max_c - min_c) : d / (max_c + min_c);
            if (std::abs(max_c - r) < 0.0001) h = ((g - b) / d + (g < b ? 6.0 : 0.0)) / 6.0;
            else if (std::abs(max_c - g) < 0.0001) h = ((b - r) / d + 2.0) / 6.0;
            else h = ((r - g) / d + 4.0) / 6.0;
        }
        *out_h = h;
        *out_s = s;
        *out_l = l;
    }

    static void HslToColor(double h, double s, double l, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept {
        if (!std::isfinite(h) || !std::isfinite(s) || !std::isfinite(l)) {
            *out_r = *out_g = *out_b = 0;
            return;
        }
        h = h - std::floor(h);
        s = std::clamp(s, 0.0, 1.0);
        l = std::clamp(l, 0.0, 1.0);
        double r, g, b;
        if (s == 0.0) {
            r = g = b = l;
        } else {
            double q = l < 0.5 ? l * (1.0 + s) : l + (1.0 - l) * s;
            double p = l < 0.5 ? l * (1.0 - s) : l - (1.0 - l) * s;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        *out_r = static_cast<uint8_t>(std::clamp(r * 255.0, 0.0, 255.0));
        *out_g = static_cast<uint8_t>(std::clamp(g * 255.0, 0.0, 255.0));
        *out_b = static_cast<uint8_t>(std::clamp(b * 255.0, 0.0, 255.0));
    }

    static double GetRelativeLuminance(uint8_t r, uint8_t g, uint8_t b) noexcept {
        const auto& table = GetLinearChannelTable();
        return 0.2126 * table[r] + 0.7152 * table[g] + 0.0722 * table[b];
    }

    static double GetContrastRatio(uint8_t r1, uint8_t g1, uint8_t b1, uint8_t r2, uint8_t g2, uint8_t b2) noexcept {
        double l1 = GetRelativeLuminance(r1, g1, b1);
        double l2 = GetRelativeLuminance(r2, g2, b2);
        if (l1 < l2) std::swap(l1, l2);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    static double ColorDistance(uint8_t r1, uint8_t g1, uint8_t b1, uint8_t r2, uint8_t g2, uint8_t b2) noexcept {
        int32_t dr = static_cast<int32_t>(r1) - static_cast<int32_t>(r2);
        int32_t dg = static_cast<int32_t>(g1) - static_cast<int32_t>(g2);
        int32_t db = static_cast<int32_t>(b1) - static_cast<int32_t>(b2);
        return std::sqrt(static_cast<double>(dr * dr + dg * dg + db * db));
    }

    static void EnsureTextOnDarkBackground(
        uint8_t cr, uint8_t cg, uint8_t cb,
        uint8_t br, uint8_t bg, uint8_t bb,
        double min_ratio,
        uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
    {
        double h = 0.0, s = 0.0, l = 0.0;
        ToHsl(cr, cg, cb, &h, &s, &l);
        uint8_t best_r = cr, best_g = cg, best_b = cb;

        if (GetRelativeLuminance(best_r, best_g, best_b) < 0.18 || l < 0.40) {
            HslToColor(h, std::max(0.18, s), std::clamp(std::max(l, 0.60), 0.55, 0.65), &best_r, &best_g, &best_b);
        }

        if (GetContrastRatio(best_r, best_g, best_b, br, bg, bb) >= min_ratio &&
            GetRelativeLuminance(best_r, best_g, best_b) >= 0.18) {
            *out_r = best_r;
            *out_g = best_g;
            *out_b = best_b;
            return;
        }

        double background_luminance = GetRelativeLuminance(br, bg, bb);
        double saturation = std::max(0.18, s);
        if (background_luminance > 0.18) {
            int32_t low = 0, high = 101;
            while (low < high) {
                int32_t step = low + (high - low) / 2;
                uint8_t cand_r = 0, cand_g = 0, cand_b = 0;
                ContrastCandidate(h, saturation, l, true, 0.55, 0.72, step, &cand_r, &cand_g, &cand_b);
                if (GetRelativeLuminance(cand_r, cand_g, cand_b) >= 0.18)
                    high = step;
                else
                    low = step + 1;
            }
            if (low > 100) {
                *out_r = 255; *out_g = 255; *out_b = 255;
                return;
            }
            uint8_t fa_r = 0, fa_g = 0, fa_b = 0;
            ContrastCandidate(h, saturation, l, true, 0.55, 0.72, low, &fa_r, &fa_g, &fa_b);
            if (GetContrastRatio(fa_r, fa_g, fa_b, br, bg, bb) >= min_ratio) {
                *out_r = fa_r; *out_g = fa_g; *out_b = fa_b;
                return;
            }
        }

        int32_t first = FindContrastStep(h, saturation, l, true, 0.55, 0.72, background_luminance, min_ratio, 0.18);
        if (first <= 100) {
            ContrastCandidate(h, saturation, l, true, 0.55, 0.72, first, out_r, out_g, out_b);
        } else {
            *out_r = 255; *out_g = 255; *out_b = 255;
        }
    }

    static void EnsureContrast(
        uint8_t sr, uint8_t sg, uint8_t sb,
        uint8_t mr, uint8_t mg, uint8_t mb,
        double min_ratio,
        uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
    {
        if (GetContrastRatio(sr, sg, sb, mr, mg, mb) >= min_ratio) {
            *out_r = sr; *out_g = sg; *out_b = sb;
            return;
        }
        double h = 0.0, s = 0.0, l = 0.0;
        ToHsl(sr, sg, sb, &h, &s, &l);
        double background_luminance = GetRelativeLuminance(mr, mg, mb);
        bool lighten = background_luminance < 0.45;
        double saturation = std::max(0.18, s);
        uint8_t init_r = 0, init_g = 0, init_b = 0;
        ContrastCandidate(h, saturation, l, lighten, 0.0, 1.0, 0, &init_r, &init_g, &init_b);
        if (GetContrastRatio(init_r, init_g, init_b, mr, mg, mb) >= min_ratio) {
            *out_r = init_r; *out_g = init_g; *out_b = init_b;
            return;
        }

        int32_t first = FindContrastStep(h, saturation, l, lighten, 0.0, 1.0, background_luminance, min_ratio, 0.0);
        if (first <= 100) {
            ContrastCandidate(h, saturation, l, lighten, 0.0, 1.0, first, out_r, out_g, out_b);
            return;
        }

        if (GetContrastRatio(255, 255, 255, mr, mg, mb) >= GetContrastRatio(0, 0, 0, mr, mg, mb)) {
            *out_r = 255; *out_g = 255; *out_b = 255;
        } else {
            *out_r = 0; *out_g = 0; *out_b = 0;
        }
    }

private:
    static constexpr bool IsHarshBlueIndigoHue(double h) noexcept {
        return h >= 0.583 && h <= 0.755;
    }

    static void RgbToHsv(float r, float g, float b, float* out_h, float* out_s, float* out_v) noexcept {
        float max_c = std::max(r, std::max(g, b));
        float min_c = std::min(r, std::min(g, b));
        float d = max_c - min_c;
        float h = 0.0f, s = max_c > 0.0f ? d / max_c : 0.0f, v = max_c;

        if (d > 0.001f) {
            if (std::abs(max_c - r) < 0.0001f) h = (g - b) / d + (g < b ? 6.0f : 0.0f);
            else if (std::abs(max_c - g) < 0.0001f) h = (b - r) / d + 2.0f;
            else h = (r - g) / d + 4.0f;
            h /= 6.0f;
        }

        *out_h = h;
        *out_s = s;
        *out_v = v;
    }

    static void HsvToColor(float h, float s, float v, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept {
        float r, g, b;
        int32_t hi = static_cast<int32_t>(h * 6.0f) % 6;
        float f = h * 6.0f - hi;
        float p = v * (1.0f - s);
        float q = v * (1.0f - f * s);
        float t = v * (1.0f - (1.0f - f) * s);

        switch (hi) {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }

        *out_r = static_cast<uint8_t>(std::clamp(r * 255.0f, 0.0f, 255.0f));
        *out_g = static_cast<uint8_t>(std::clamp(g * 255.0f, 0.0f, 255.0f));
        *out_b = static_cast<uint8_t>(std::clamp(b * 255.0f, 0.0f, 255.0f));
    }

    static double HueToRgb(double p, double q, double t) noexcept {
        if (t < 0.0) t += 1.0;
        if (t > 1.0) t -= 1.0;
        if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
        if (t <= 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return q - (q - p) * (6.0 * t - 3.0);
        return p;
    }

    static const std::array<double, 256>& GetLinearChannelTable() noexcept {
        static const std::array<double, 256> table = []() {
            std::array<double, 256> t{};
            for (int32_t v = 0; v < 256; v++) {
                double x = v / 255.0;
                t[v] = x <= 0.03928 ? x / 12.92 : std::pow((x + 0.055) / 1.055, 2.4);
            }
            return t;
        }();
        return table;
    }

    static void ContrastCandidate(
        double hue, double saturation, double lightness,
        bool lighten, double lower_lightness, double upper_lightness, int32_t step,
        uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
    {
        double t = step / 100.0;
        double l = lighten ? lightness + (1.0 - lightness) * t : lightness * (1.0 - t);
        HslToColor(hue, saturation, std::clamp(l, lower_lightness, upper_lightness), out_r, out_g, out_b);
    }

    static int32_t FindContrastStep(
        double hue, double saturation, double lightness,
        bool lighten, double lower_lightness, double upper_lightness, double background_luminance,
        double min_ratio, double minimum_luminance) noexcept
    {
        int32_t low = 0, high = 101;
        while (low < high) {
            int32_t step = low + (high - low) / 2;
            uint8_t cr = 0, cg = 0, cb = 0;
            ContrastCandidate(hue, saturation, lightness, lighten, lower_lightness, upper_lightness, step, &cr, &cg, &cb);
            double luminance = GetRelativeLuminance(cr, cg, cb);
            bool meets_contrast = lighten
                ? luminance >= background_luminance && (luminance + 0.05) / (background_luminance + 0.05) >= min_ratio
                : luminance <= background_luminance && (background_luminance + 0.05) / (luminance + 0.05) >= min_ratio;
            if (luminance >= minimum_luminance && meets_contrast)
                high = step;
            else
                low = step + 1;
        }
        return low;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_dicolor_get_clamped_crop_rect(
    double left, double top, double right, double bottom, uint8_t is_empty,
    int32_t pixel_width, int32_t pixel_height,
    int32_t* out_x, int32_t* out_y, int32_t* out_w, int32_t* out_h, uint8_t* out_valid) noexcept
{
    if (!out_x || !out_y || !out_w || !out_h || !out_valid) return VN_ERR_INVALID_ARG;
    *out_x = 0; *out_y = 0; *out_w = 0; *out_h = 0;
    *out_valid = vnotch::services::DynamicIslandColorExtractor::GetClampedCropRect(
        left, top, right, bottom, is_empty, pixel_width, pixel_height, out_x, out_y, out_w, out_h) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_ensure_bright(
    uint8_t r, uint8_t g, uint8_t b, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    vnotch::services::DynamicIslandColorExtractor::EnsureBrightColor(r, g, b, out_r, out_g, out_b);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_get_vibrant(
    uint8_t r, uint8_t g, uint8_t b, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    vnotch::services::DynamicIslandColorExtractor::GetVibrantColor(r, g, b, out_r, out_g, out_b);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_get_adaptive_blur_opacity(
    double luminance, double brightness_boost, double* out_opacity) noexcept
{
    if (!out_opacity) return VN_ERR_INVALID_ARG;
    *out_opacity = vnotch::services::DynamicIslandColorExtractor::GetAdaptiveBlurOpacity(luminance, brightness_boost);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_get_adaptive_blur_image_opacity(
    double luminance, double* out_opacity) noexcept
{
    if (!out_opacity) return VN_ERR_INVALID_ARG;
    *out_opacity = vnotch::services::DynamicIslandColorExtractor::GetAdaptiveBlurImageOpacity(luminance);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_compute_brightness_dim(
    const uint8_t* pixels, int32_t buf_len, int32_t pixel_count, double* out_dim) noexcept
{
    if (!out_dim) return VN_ERR_INVALID_ARG;
    if (!pixels || buf_len <= 0 || pixel_count <= 0) {
        *out_dim = 0.0;
        return VN_OK;
    }
    *out_dim = vnotch::services::DynamicIslandColorExtractor::ComputeBrightnessDim(
        std::span<const uint8_t>(pixels, static_cast<size_t>(buf_len)), pixel_count);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_extract_advanced_palette(
    const uint8_t* pixels, int32_t width, int32_t height, int32_t stride,
    vnotch::services::VnPaletteResult* out_res) noexcept
{
    if (!out_res) return VN_ERR_INVALID_ARG;
    vnotch::services::DynamicIslandColorExtractor::ExtractAdvancedPalette(pixels, width, height, stride, out_res);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_to_hsl(
    uint8_t r, uint8_t g, uint8_t b, double* out_h, double* out_s, double* out_l) noexcept
{
    if (!out_h || !out_s || !out_l) return VN_ERR_INVALID_ARG;
    vnotch::services::DynamicIslandColorExtractor::ToHsl(r, g, b, out_h, out_s, out_l);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_hsl_to_color(
    double h, double s, double l, uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    vnotch::services::DynamicIslandColorExtractor::HslToColor(h, s, l, out_r, out_g, out_b);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_get_relative_luminance(
    uint8_t r, uint8_t g, uint8_t b, double* out_lum) noexcept
{
    if (!out_lum) return VN_ERR_INVALID_ARG;
    *out_lum = vnotch::services::DynamicIslandColorExtractor::GetRelativeLuminance(r, g, b);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_get_contrast_ratio(
    uint8_t r1, uint8_t g1, uint8_t b1, uint8_t r2, uint8_t g2, uint8_t b2, double* out_ratio) noexcept
{
    if (!out_ratio) return VN_ERR_INVALID_ARG;
    *out_ratio = vnotch::services::DynamicIslandColorExtractor::GetContrastRatio(r1, g1, b1, r2, g2, b2);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_color_distance(
    uint8_t r1, uint8_t g1, uint8_t b1, uint8_t r2, uint8_t g2, uint8_t b2, double* out_dist) noexcept
{
    if (!out_dist) return VN_ERR_INVALID_ARG;
    *out_dist = vnotch::services::DynamicIslandColorExtractor::ColorDistance(r1, g1, b1, r2, g2, b2);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_ensure_text_on_dark_bg(
    uint8_t cr, uint8_t cg, uint8_t cb, uint8_t br, uint8_t bg, uint8_t bb, double min_ratio,
    uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    vnotch::services::DynamicIslandColorExtractor::EnsureTextOnDarkBackground(cr, cg, cb, br, bg, bb, min_ratio, out_r, out_g, out_b);
    return VN_OK;
}

VN_EXPORT int32_t vn_dicolor_ensure_contrast(
    uint8_t sr, uint8_t sg, uint8_t sb, uint8_t mr, uint8_t mg, uint8_t mb, double min_ratio,
    uint8_t* out_r, uint8_t* out_g, uint8_t* out_b) noexcept
{
    if (!out_r || !out_g || !out_b) return VN_ERR_INVALID_ARG;
    vnotch::services::DynamicIslandColorExtractor::EnsureContrast(sr, sg, sb, mr, mg, mb, min_ratio, out_r, out_g, out_b);
    return VN_OK;
}

} // extern "C"
