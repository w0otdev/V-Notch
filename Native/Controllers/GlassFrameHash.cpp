#include "vnotch_common.h"
#include "vnotch_cpu.h"
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <immintrin.h>
#include <limits>
#include <string_view>

#include "GlassOpticsMath.h"

extern "C" {
VN_EXPORT int32_t __stdcall vn_glass_frame_hash_128(
    const uint8_t* data,
    int32_t length,
    uint64_t* out_lo,
    uint64_t* out_hi) noexcept
{
    if (!out_lo || !out_hi)
    {
        return VN_ERR_INVALID_ARG;
    }
    if (!data || length <= 0)
    {
        *out_lo = 0ULL;
        *out_hi = 0ULL;
        return VN_OK;
    }

    constexpr uint64_t kPrimeLo = 0x9E3779B185EBCA87ULL;
    constexpr uint64_t kPrimeHi = 0xC2B2AE3D27D4EB4FULL;
    constexpr uint64_t kIdxPrime = 0x165667B19E3779F9ULL;

    uint64_t lo = 0xCBF29CE484222325ULL ^ static_cast<uint64_t>(length);
    uint64_t hi = 0x84222325CBF29CE4ULL ^ (static_cast<uint64_t>(length) * kPrimeLo);

    int32_t offset = 0;
    if (length >= 64 && vnotch::cpu::SupportsX64V3())
    {
        __m256i acc_lo0 = _mm256_set_epi64x(
            static_cast<int64_t>(lo ^ (kPrimeLo * 1ULL)),
            static_cast<int64_t>(lo ^ (kPrimeLo * 3ULL)),
            static_cast<int64_t>(lo ^ (kPrimeLo * 5ULL)),
            static_cast<int64_t>(lo ^ (kPrimeLo * 7ULL)));
        __m256i acc_hi0 = _mm256_set_epi64x(
            static_cast<int64_t>(hi ^ (kPrimeHi * 1ULL)),
            static_cast<int64_t>(hi ^ (kPrimeHi * 3ULL)),
            static_cast<int64_t>(hi ^ (kPrimeHi * 5ULL)),
            static_cast<int64_t>(hi ^ (kPrimeHi * 7ULL)));
        __m256i acc_lo1 = _mm256_xor_si256(acc_lo0, _mm256_set1_epi64x(static_cast<int64_t>(kIdxPrime)));
        __m256i acc_hi1 = _mm256_xor_si256(acc_hi0, _mm256_set1_epi64x(static_cast<int64_t>(kPrimeLo)));

        const __m256i mult_lo = _mm256_set1_epi32(static_cast<int32_t>(0x85EBCA87u));
        const __m256i mult_hi = _mm256_set1_epi32(static_cast<int32_t>(0xC2B2AE3Du));
        __m256i idx_vec = _mm256_set_epi64x(4, 3, 2, 1);
        const __m256i idx_step = _mm256_set1_epi64x(static_cast<int64_t>(kIdxPrime));

        const int32_t limit64 = length - 63;
        for (; offset < limit64; offset += 64)
        {
            const __m256i v0 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(data + offset));
            const __m256i v1 = _mm256_loadu_si256(reinterpret_cast<const __m256i*>(data + offset + 32));

            const __m256i x0 = _mm256_xor_si256(v0, idx_vec);
            idx_vec = _mm256_add_epi64(idx_vec, idx_step);
            const __m256i x1 = _mm256_xor_si256(v1, idx_vec);
            idx_vec = _mm256_add_epi64(idx_vec, idx_step);

            const __m256i p0_lo = _mm256_mul_epu32(x0, mult_lo);
            const __m256i p0_hi = _mm256_mul_epu32(_mm256_srli_epi64(x0, 32), mult_hi);
            const __m256i p1_lo = _mm256_mul_epu32(x1, mult_lo);
            const __m256i p1_hi = _mm256_mul_epu32(_mm256_srli_epi64(x1, 32), mult_hi);

            acc_lo0 = _mm256_xor_si256(acc_lo0, _mm256_add_epi64(v0, p0_hi));
            acc_lo0 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_lo0, 31), _mm256_srli_epi64(acc_lo0, 33)),
                p0_lo);

            acc_hi0 = _mm256_xor_si256(acc_hi0, _mm256_add_epi64(x0, p0_lo));
            acc_hi0 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_hi0, 27), _mm256_srli_epi64(acc_hi0, 37)),
                p0_hi);

            acc_lo1 = _mm256_xor_si256(acc_lo1, _mm256_add_epi64(v1, p1_hi));
            acc_lo1 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_lo1, 31), _mm256_srli_epi64(acc_lo1, 33)),
                p1_lo);

            acc_hi1 = _mm256_xor_si256(acc_hi1, _mm256_add_epi64(x1, p1_lo));
            acc_hi1 = _mm256_add_epi64(
                _mm256_xor_si256(_mm256_slli_epi64(acc_hi1, 27), _mm256_srli_epi64(acc_hi1, 37)),
                p1_hi);
        }

        alignas(32) uint64_t lanes_lo0[4];
        alignas(32) uint64_t lanes_lo1[4];
        alignas(32) uint64_t lanes_hi0[4];
        alignas(32) uint64_t lanes_hi1[4];
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_lo0), acc_lo0);
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_lo1), acc_lo1);
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_hi0), acc_hi0);
        _mm256_store_si256(reinterpret_cast<__m256i*>(lanes_hi1), acc_hi1);

        lo = vnotch::controllers::Avalanche64(lanes_lo0[0] ^ lanes_lo1[1]) ^
             vnotch::controllers::Avalanche64(lanes_lo0[2] ^ lanes_lo1[3]) ^
             vnotch::controllers::Avalanche64(lanes_lo1[0] ^ lanes_lo0[1]) ^
             vnotch::controllers::Avalanche64(lanes_lo1[2] ^ lanes_lo0[3]);
        hi = vnotch::controllers::Avalanche64(lanes_hi0[0] ^ lanes_hi1[1]) ^
             vnotch::controllers::Avalanche64(lanes_hi0[2] ^ lanes_hi1[3]) ^
             vnotch::controllers::Avalanche64(lanes_hi1[0] ^ lanes_hi0[1]) ^
             vnotch::controllers::Avalanche64(lanes_hi1[2] ^ lanes_hi0[3]);
    }

    for (; offset <= length - 8; offset += 8)
    {
        uint64_t w = 0;
        std::memcpy(&w, data + offset, sizeof(w));
        uint64_t idx_mix = static_cast<uint64_t>((offset >> 3) + 1) * kIdxPrime;
        lo = _rotl64(lo ^ (w + idx_mix), 31) * kPrimeLo;
        hi = _rotl64(hi ^ (w ^ idx_mix), 27) * kPrimeHi;
    }

    for (; offset < length; ++offset)
    {
        uint64_t b = static_cast<uint64_t>(data[offset]) + 1ULL;
        uint64_t pos = static_cast<uint64_t>(offset + 1) * 0x27D4EB2F165667C5ULL;
        lo = vnotch::controllers::Avalanche64(lo ^ (b * pos)) * kPrimeLo;
        hi = vnotch::controllers::Avalanche64(hi ^ ((b << 8) ^ pos)) * kPrimeHi;
    }

    *out_lo = vnotch::controllers::Avalanche64(lo ^ hi);
    *out_hi = vnotch::controllers::Avalanche64(hi ^ (lo * kPrimeLo));
    return VN_OK;
}


}
