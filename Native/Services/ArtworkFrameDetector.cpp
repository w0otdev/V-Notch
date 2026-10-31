#include "vnotch_common.h"

namespace vnotch::services
{
    class ArtworkFrameDetector final
    {
    public:
        struct CandidateScore
        {
            int32_t position;
            double score;
        };

        static double Brightness(const uint8_t* pixels, int32_t w, int32_t x, int32_t y) noexcept
        {
            int64_t i = (static_cast<int64_t>(y) * w + x) * 4LL;
            return (static_cast<int32_t>(pixels[i]) +
                    static_cast<int32_t>(pixels[i + 1]) +
                    static_cast<int32_t>(pixels[i + 2])) / 3.0;
        }

        static double EdgeSupport(const uint8_t* pixels, int32_t w, int32_t position, bool row, int32_t direction, int32_t start, int32_t end) noexcept
        {
            if (end <= start) return 0.0;
            int32_t supported = 0;
            for (int32_t p = start; p < end; ++p)
            {
                double inside = Brightness(pixels, w, row ? p : position + direction * 2, row ? position + direction * 2 : p);
                double outside = Brightness(pixels, w, row ? p : position - direction * 2, row ? position - direction * 2 : p);
                if (outside <= 85.0 && inside - outside >= 30.0)
                {
                    ++supported;
                }
            }
            return static_cast<double>(supported) / static_cast<double>(end - start);
        }

        static void FindCandidates(
            const uint8_t* pixels,
            int32_t w,
            int32_t h,
            bool row,
            int32_t direction,
            int32_t out_selected[4],
            int32_t& out_count)
        {
            out_count = 0;
            int32_t length = row ? h : w;
            int32_t across = row ? w : h;
            int32_t start = direction > 0 ? std::max(3, length / 25) : (length * 3) / 5;
            int32_t end = direction > 0 ? (length * 2) / 5 : std::min(length - 3, (length * 24) / 25);
            if (start >= end)
            {
                return;
            }

            std::vector<CandidateScore> scores;
            scores.reserve(static_cast<size_t>(end - start));
            int32_t ac_start = (across * 2) / 5;
            int32_t ac_end = (across * 3) / 5;
            for (int32_t p = start; p < end; ++p)
            {
                scores.push_back({ p, EdgeSupport(pixels, w, p, row, direction, ac_start, ac_end) });
            }

            std::stable_sort(scores.begin(), scores.end(), [](const CandidateScore& a, const CandidateScore& b) {
                return a.score > b.score;
            });

            for (const CandidateScore& candidate : scores)
            {
                if (candidate.score < 0.8)
                {
                    break;
                }
                bool far_enough = true;
                for (int32_t i = 0; i < out_count; ++i)
                {
                    if (std::abs(out_selected[i] - candidate.position) <= 3)
                    {
                        far_enough = false;
                        break;
                    }
                }
                if (far_enough)
                {
                    out_selected[out_count++] = candidate.position;
                    if (out_count == 4)
                    {
                        break;
                    }
                }
            }
        }

        static bool DetectDarkTexturedFrameCore(
            const uint8_t* pixels,
            int32_t w,
            int32_t h,
            int32_t width,
            int32_t height,
            int32_t& out_x,
            int32_t& out_y,
            int32_t& out_w,
            int32_t& out_h)
        {
            int32_t lefts[4]{}, rights[4]{}, tops[4]{}, bottoms[4]{};
            int32_t n_lefts = 0, n_rights = 0, n_tops = 0, n_bottoms = 0;
            FindCandidates(pixels, w, h, false, 1, lefts, n_lefts);
            FindCandidates(pixels, w, h, false, -1, rights, n_rights);
            FindCandidates(pixels, w, h, true, 1, tops, n_tops);
            FindCandidates(pixels, w, h, true, -1, bottoms, n_bottoms);

            bool found = false;
            double best_score = 0.0;

            for (int32_t il = 0; il < n_lefts; ++il)
            {
                int32_t left = lefts[il];
                for (int32_t ir = 0; ir < n_rights; ++ir)
                {
                    int32_t right = rights[ir];
                    for (int32_t it = 0; it < n_tops; ++it)
                    {
                        int32_t top = tops[it];
                        for (int32_t ib = 0; ib < n_bottoms; ++ib)
                        {
                            int32_t bottom = bottoms[ib];
                            int32_t cw = right - left;
                            int32_t ch = bottom - top;
                            if (cw <= 0 || ch <= 0)
                            {
                                continue;
                            }
                            double aspect = static_cast<double>(cw) / static_cast<double>(ch);
                            if (aspect < 0.85 || aspect > 1.15 || cw < w * 0.3 || ch < h * 0.45)
                            {
                                continue;
                            }
                            double s_left = EdgeSupport(pixels, w, left, false, 1, top + 3, bottom - 3);
                            double s_right = EdgeSupport(pixels, w, right, false, -1, top + 3, bottom - 3);
                            double s_top = EdgeSupport(pixels, w, top, true, 1, left + 3, right - 3);
                            double s_bottom = EdgeSupport(pixels, w, bottom, true, -1, left + 3, right - 3);
                            double min_support = std::min(std::min(s_left, s_right), std::min(s_top, s_bottom));
                            if (min_support < 0.85)
                            {
                                continue;
                            }
                            double avg_support = (s_left + s_right + s_top + s_bottom) * 0.25;
                            double score = avg_support + 0.05 * static_cast<double>(cw * ch) / static_cast<double>(w * h);
                            if (score <= best_score)
                            {
                                continue;
                            }
                            int32_t x = static_cast<int32_t>(std::ceil((left + 3.0) * width / w));
                            int32_t y = static_cast<int32_t>(std::ceil((top + 3.0) * height / h));
                            int32_t x2 = static_cast<int32_t>(std::floor((right - 3.0) * width / w));
                            int32_t y2 = static_cast<int32_t>(std::floor((bottom - 3.0) * height / h));
                            out_x = x;
                            out_y = y;
                            out_w = x2 - x;
                            out_h = y2 - y;
                            best_score = score;
                            found = true;
                        }
                    }
                }
            }
            return found;
        }

        static bool IsWhite(const uint8_t* pixels, int32_t w, int32_t x, int32_t y) noexcept
        {
            int64_t i = (static_cast<int64_t>(y) * w + x) * 4LL;
            int32_t b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            int32_t mn = std::min(b, std::min(g, r));
            int32_t mx = std::max(b, std::max(g, r));
            return a >= 240 && mn >= 225 && (mx - mn) <= 24;
        }

        static bool IsMatLine(const uint8_t* pixels, int32_t w, int32_t h, int32_t pad_x, int32_t pad_y, int32_t position, bool row) noexcept
        {
            int32_t start = row ? pad_x : pad_y;
            int32_t end = row ? (w - pad_x) : (h - pad_y);
            int32_t white = 0;
            for (int32_t p = start; p < end; ++p)
            {
                if (IsWhite(pixels, w, row ? p : position, row ? position : p))
                {
                    ++white;
                }
            }
            return white >= (end - start) * 0.85;
        }

        static bool HasPictureEdge(const uint8_t* pixels, int32_t w, int32_t left, int32_t right, int32_t top, int32_t bottom, int32_t position, bool row) noexcept
        {
            int32_t start = row ? left : top;
            int32_t end = row ? right : bottom;
            int32_t content = 0;
            for (int32_t p = start; p <= end; ++p)
            {
                if (!IsWhite(pixels, w, row ? p : position, row ? position : p))
                {
                    ++content;
                }
            }
            return content >= (end - start + 1) * 0.7;
        }

        static bool DetectWhiteFrameCore(
            const uint8_t* pixels,
            int32_t w,
            int32_t h,
            int32_t width,
            int32_t height,
            int32_t& out_x,
            int32_t& out_y,
            int32_t& out_w,
            int32_t& out_h) noexcept
        {
            int32_t pad_x = std::max(1, static_cast<int32_t>(std::ceil(w * 0.02)));
            int32_t pad_y = std::max(1, static_cast<int32_t>(std::ceil(h * 0.02)));
            int32_t left = pad_x;
            int32_t right = w - pad_x - 1;
            int32_t top = pad_y;
            int32_t bottom = h - pad_y - 1;

            while (left < w / 3 && IsMatLine(pixels, w, h, pad_x, pad_y, left, false)) ++left;
            while (right > (w * 2) / 3 && IsMatLine(pixels, w, h, pad_x, pad_y, right, false)) --right;
            while (top < h / 3 && IsMatLine(pixels, w, h, pad_x, pad_y, top, true)) ++top;
            while (bottom > (h * 2) / 3 && IsMatLine(pixels, w, h, pad_x, pad_y, bottom, true)) --bottom;

            if (left <= pad_x || right >= w - pad_x - 1 || top <= pad_y || bottom >= h - pad_y - 1 ||
                left >= w / 3 || right <= (w * 2) / 3 || top >= h / 3 || bottom <= (h * 2) / 3)
            {
                return false;
            }

            if (!HasPictureEdge(pixels, w, left, right, top, bottom, left, false) ||
                !HasPictureEdge(pixels, w, left, right, top, bottom, right, false) ||
                !HasPictureEdge(pixels, w, left, right, top, bottom, top, true) ||
                !HasPictureEdge(pixels, w, left, right, top, bottom, bottom, true))
            {
                return false;
            }

            int32_t x = static_cast<int32_t>(std::ceil((left + 1.0) * width / w));
            int32_t y = static_cast<int32_t>(std::ceil((top + 1.0) * height / h));
            int32_t x2 = static_cast<int32_t>(std::floor(right * static_cast<double>(width) / w));
            int32_t y2 = static_cast<int32_t>(std::floor(bottom * static_cast<double>(height) / h));
            out_x = x;
            out_y = y;
            out_w = x2 - x;
            out_h = y2 - y;
            return true;
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_artwork_frame_detect_white(
    const uint8_t* pixels,
    int32_t w,
    int32_t h,
    int32_t width,
    int32_t height,
    int32_t* out_x,
    int32_t* out_y,
    int32_t* out_w,
    int32_t* out_h,
    uint8_t* out_found) noexcept
{
    if (!pixels || w < 8 || h < 8 || w > 16384 || h > 16384 || width <= 0 || height <= 0 || !out_x || !out_y || !out_w || !out_h || !out_found)
        return VN_ERR_INVALID_ARG;
    *out_x = *out_y = *out_w = *out_h = 0;
    try {
        *out_found = vnotch::services::ArtworkFrameDetector::DetectWhiteFrameCore(
            pixels, w, h, width, height, *out_x, *out_y, *out_w, *out_h) ? 1u : 0u;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_artwork_frame_detect_dark_textured(
    const uint8_t* pixels,
    int32_t w,
    int32_t h,
    int32_t width,
    int32_t height,
    int32_t* out_x,
    int32_t* out_y,
    int32_t* out_w,
    int32_t* out_h,
    uint8_t* out_found) noexcept
{
    if (!pixels || w < 8 || h < 8 || w > 16384 || h > 16384 || width <= 0 || height <= 0 || !out_x || !out_y || !out_w || !out_h || !out_found)
        return VN_ERR_INVALID_ARG;
    *out_x = *out_y = *out_w = *out_h = 0;
    try {
        *out_found = vnotch::services::ArtworkFrameDetector::DetectDarkTexturedFrameCore(
            pixels, w, h, width, height, *out_x, *out_y, *out_w, *out_h) ? 1u : 0u;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t vn_artwork_frame_detect_any(
    const uint8_t* pixels,
    int32_t w,
    int32_t h,
    int32_t width,
    int32_t height,
    int32_t* out_x,
    int32_t* out_y,
    int32_t* out_w,
    int32_t* out_h,
    uint8_t* out_found) noexcept
{
    if (!pixels || w < 8 || h < 8 || w > 16384 || h > 16384 || width <= 0 || height <= 0 || !out_x || !out_y || !out_w || !out_h || !out_found)
        return VN_ERR_INVALID_ARG;
    *out_x = *out_y = *out_w = *out_h = 0;
    try {
        bool ok = vnotch::services::ArtworkFrameDetector::DetectWhiteFrameCore(
                      pixels, w, h, width, height, *out_x, *out_y, *out_w, *out_h) ||
                  vnotch::services::ArtworkFrameDetector::DetectDarkTexturedFrameCore(
                      pixels, w, h, width, height, *out_x, *out_y, *out_w, *out_h);
        *out_found = ok ? 1u : 0u;
        return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
