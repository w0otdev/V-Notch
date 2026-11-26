#include "../vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <cstring>

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
        const int64_t row_bytes = static_cast<int64_t>(width) * 4LL;
        const int32_t words = static_cast<int32_t>(row_bytes >> 3);
        const int32_t step = std::max(1, words / 96);
        const int32_t row_step = std::max(1, height / 96);

        for (int32_t y = 0; y < height; y += row_step)
        {
            const uint8_t* row = pixels + static_cast<int64_t>(y) * row_bytes;
            int32_t i = 0;
            for (; i + 3 * step < words; i += 4 * step)
            {
                a = (a ^ ReadU64(row + static_cast<int64_t>(i) * 8)) * prime;
                b = (b ^ ReadU64(row + static_cast<int64_t>(i + step) * 8)) * prime;
                c = (c ^ ReadU64(row + static_cast<int64_t>(i + 2 * step) * 8)) * prime;
                d = (d ^ ReadU64(row + static_cast<int64_t>(i + 3 * step) * 8)) * prime;
            }
            for (; i < words; i += step)
            {
                a = (a ^ ReadU64(row + static_cast<int64_t>(i) * 8)) * prime;
            }
            if ((width & 1) != 0)
            {
                d = (d ^ ReadU32(row + static_cast<int64_t>(width - 1) * 4)) * prime;
            }
        }

        *out_hash = (((((a * prime) ^ b) * prime) ^ c) * prime) ^ d;
        return VN_OK;
    }

private:
    static inline uint64_t ReadU64(const uint8_t* ptr) noexcept
    {
        uint64_t value;
        std::memcpy(&value, ptr, sizeof(value));
        return value;
    }

    static inline uint32_t ReadU32(const uint8_t* ptr) noexcept
    {
        uint32_t value;
        std::memcpy(&value, ptr, sizeof(value));
        return value;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t __stdcall vn_glass_frame_fingerprint_compute(
    const uint8_t* pixels,
    int32_t width,
    int32_t height,
    uint64_t* out_hash) noexcept
{
    return vnotch::controllers::GlassFrameFingerprint::Compute(pixels, width, height, out_hash);
}

} // extern "C"
