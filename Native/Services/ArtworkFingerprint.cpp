#include "vnotch_common.h"

namespace vnotch::services
{
    class ArtworkFingerprint final
    {
    public:
        static constexpr uint64_t FnvOffsetBasis = 14695981039346656037ULL;
        static constexpr uint64_t FnvPrime = 1099511628211ULL;

        static constexpr void MixInt(uint64_t& hash, int32_t value) noexcept
        {
            uint32_t u = static_cast<uint32_t>(value);
            hash ^= static_cast<uint8_t>(u & 0xFFu);
            hash *= FnvPrime;
            hash ^= static_cast<uint8_t>((u >> 8) & 0xFFu);
            hash *= FnvPrime;
            hash ^= static_cast<uint8_t>((u >> 16) & 0xFFu);
            hash *= FnvPrime;
            hash ^= static_cast<uint8_t>((u >> 24) & 0xFFu);
            hash *= FnvPrime;
        }

        static int32_t ComputeHash(
            const uint8_t* pixels,
            int32_t byte_count,
            int32_t source_width,
            int32_t source_height,
            int32_t sampled_width,
            int32_t sampled_height,
            uint64_t* out_hash) noexcept
        {
            if (out_hash == nullptr || byte_count < 0 || (byte_count > 0 && pixels == nullptr))
            {
                return VN_ERR_INVALID_ARG;
            }

            uint64_t hash = FnvOffsetBasis;
            MixInt(hash, source_width);
            MixInt(hash, source_height);
            MixInt(hash, sampled_width);
            MixInt(hash, sampled_height);

            std::span<const uint8_t> data(pixels, static_cast<size_t>(byte_count));
            for (uint8_t b : data)
            {
                hash ^= b;
                hash *= FnvPrime;
            }

            *out_hash = hash;
            return VN_OK;
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_artwork_fingerprint_compute(
    const uint8_t* pixels,
    int32_t byte_count,
    int32_t source_width,
    int32_t source_height,
    int32_t sampled_width,
    int32_t sampled_height,
    uint64_t* out_hash) noexcept
{
    return vnotch::services::ArtworkFingerprint::ComputeHash(
        pixels, byte_count, source_width, source_height, sampled_width, sampled_height, out_hash);
}
