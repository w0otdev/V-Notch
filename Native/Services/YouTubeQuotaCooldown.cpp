#include "vnotch_common.h"
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <bcrypt.h>
#include <array>
#include <cstdint>
#include <string>
#include <string_view>

#pragma comment(lib, "bcrypt.lib")

namespace vnotch::services {

class YouTubeQuotaCooldown final {
public:
    static bool HashKey(std::u16string_view key, char16_t* out_hex64) noexcept {
        std::u16string_view trimmed = vnotch::trim_view(key);

        std::string utf8;
        if (!trimmed.empty()) {
            int req = WideCharToMultiByte(
                CP_UTF8, 0,
                reinterpret_cast<LPCWCH>(trimmed.data()),
                static_cast<int>(trimmed.size()),
                nullptr, 0, nullptr, nullptr);
            if (req > 0) {
                utf8.resize(static_cast<size_t>(req));
                WideCharToMultiByte(
                    CP_UTF8, 0,
                    reinterpret_cast<LPCWCH>(trimmed.data()),
                    static_cast<int>(trimmed.size()),
                    utf8.data(), req, nullptr, nullptr);
            }
        }

        std::array<uint8_t, 32> digest{};
        BCRYPT_ALG_HANDLE alg = nullptr;
        if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) {
            return false;
        }
        NTSTATUS status = BCryptHash(
            alg, nullptr, 0,
            reinterpret_cast<PUCHAR>(utf8.data()),
            static_cast<ULONG>(utf8.size()),
            digest.data(),
            static_cast<ULONG>(digest.size()));
        BCryptCloseAlgorithmProvider(alg, 0);
        if (status != 0) {
            return false;
        }

        static constexpr char16_t kHex[] = u"0123456789ABCDEF";
        for (size_t i = 0; i < 32; i++) {
            uint8_t b = digest[i];
            out_hex64[i * 2] = kHex[(b >> 4) & 0x0F];
            out_hex64[i * 2 + 1] = kHex[b & 0x0F];
        }
        return true;
    }

    static constexpr int64_t ComputeUntilTicks(int64_t now_ticks) noexcept {
        int64_t date_ticks = now_ticks - (now_ticks % vnotch::TICKS_PER_DAY);
        return date_ticks + vnotch::TICKS_PER_DAY + 8LL * vnotch::TICKS_PER_HOUR;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_youtube_quota_hash_key(
    const char16_t* key_ptr, int32_t key_len, char16_t* out_hex64) noexcept
{
    if (!out_hex64) return VN_ERR_INVALID_ARG;
    try {
        std::u16string_view key_v = (key_ptr && key_len > 0)
            ? std::u16string_view(key_ptr, static_cast<size_t>(key_len))
            : std::u16string_view{};
        if (!vnotch::services::YouTubeQuotaCooldown::HashKey(key_v, out_hex64)) {
            return VN_ERR_FAIL;
        }
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_youtube_quota_compute_until_ticks(
    int64_t now_ticks, int64_t* out_until_ticks) noexcept
{
    if (!out_until_ticks) return VN_ERR_INVALID_ARG;
    *out_until_ticks = vnotch::services::YouTubeQuotaCooldown::ComputeUntilTicks(now_ticks);
    return VN_OK;
}

} // extern "C"
