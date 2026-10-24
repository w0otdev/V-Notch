#include "../vnotch_common.h"
#include <algorithm>
#include <cstdint>

namespace vnotch::controllers
{

class GlassFrameFingerprint final
{
public:
    static int32_t Compute(
        const uint8_t* pixels,
        int32_t width,
        int32_t height,
        uint64_t* out_hash) noexcept
    {
        if (out_hash == nullptr)
        {
            return VN_ERR_INVALID_ARG;
        }
        if (pixels == nullptr || width <= 0 || height <= 0)
        {
            *out_hash = 0ULL;
            return VN_OK;
        }

        constexpr uint64_t prime = 1099511628211ULL;
        constexpr uint64_t seed = 14695981039346656037ULL;
        uint64_t a = seed, b = seed, c = seed, d = seed;
        int64_t row_bytes = static_cast<int64_t>(width) * 4LL;
        int32_t words = static_cast<int32_t>(row_bytes >> 3);
        int32_t step = std::max(1, words / 96);
        int32_t row_step = std::max(1, height / 96);

        for (int32_t y = 0; y < height; y += row_step)
        {
            const uint64_t* row = reinterpret_cast<const uint64_t*>(pixels + static_cast<int64_t>(y) * row_bytes);
            int32_t i = 0;
            for (; i + 3 * step < words; i += 4 * step)
            {
                a = (a ^ row[i]) * prime;
                b = (b ^ row[i + step]) * prime;
                c = (c ^ row[i + 2 * step]) * prime;
                d = (d ^ row[i + 3 * step]) * prime;
            }
            for (; i < words; i += step)
            {
                a = (a ^ row[i]) * prime;
            }
            if ((width & 1) != 0)
            {
                d = (d ^ reinterpret_cast<const uint32_t*>(row)[width - 1]) * prime;
            }
        }

        *out_hash = (((((a * prime) ^ b) * prime) ^ c) * prime) ^ d;
        return VN_OK;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t vn_glass_frame_fingerprint_compute(
    const uint8_t* pixels,
    int32_t width,
    int32_t height,
    uint64_t* out_hash) noexcept
{
    return vnotch::controllers::GlassFrameFingerprint::Compute(pixels, width, height, out_hash);
}

} // extern "C"
