#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class SpotifyTokenProvider final
    {
    public:
        static inline uint32_t rotl32(uint32_t x, int n) noexcept
        {
            return (x << n) | (x >> (32 - n));
        }
        
        static inline void sha1_compute(const uint8_t* data, size_t len, uint8_t out_digest[20]) noexcept
        {
            uint32_t h0 = 0x67452301u;
            uint32_t h1 = 0xEFCDAB89u;
            uint32_t h2 = 0x98BADCFEu;
            uint32_t h3 = 0x10325476u;
            uint32_t h4 = 0xC3D2E1F0u;
        
            auto process_block = [&](const uint8_t block[64]) noexcept {
                uint32_t w[80];
                for (int i = 0; i < 16; ++i)
                {
                    w[i] = (static_cast<uint32_t>(block[i * 4]) << 24) |
                           (static_cast<uint32_t>(block[i * 4 + 1]) << 16) |
                           (static_cast<uint32_t>(block[i * 4 + 2]) << 8) |
                           static_cast<uint32_t>(block[i * 4 + 3]);
                }
                for (int i = 16; i < 80; ++i)
                {
                    w[i] = rotl32(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
                }
        
                uint32_t a = h0;
                uint32_t b = h1;
                uint32_t c = h2;
                uint32_t d = h3;
                uint32_t e = h4;
        
                for (int i = 0; i < 80; ++i)
                {
                    uint32_t f = 0;
                    uint32_t k = 0;
                    if (i < 20)
                    {
                        f = (b & c) | ((~b) & d);
                        k = 0x5A827999u;
                    }
                    else if (i < 40)
                    {
                        f = b ^ c ^ d;
                        k = 0x6ED9EBA1u;
                    }
                    else if (i < 60)
                    {
                        f = (b & c) | (b & d) | (c & d);
                        k = 0x8F1BBCDCu;
                    }
                    else
                    {
                        f = b ^ c ^ d;
                        k = 0xCA62C1D6u;
                    }
                    uint32_t temp = rotl32(a, 5) + f + e + k + w[i];
                    e = d;
                    d = c;
                    c = rotl32(b, 30);
                    b = a;
                    a = temp;
                }
        
                h0 += a;
                h1 += b;
                h2 += c;
                h3 += d;
                h4 += e;
            };
        
            size_t offset = 0;
            while (offset + 64 <= len)
            {
                process_block(data + offset);
                offset += 64;
            }
        
            uint8_t tail[128]{};
            size_t rem = len - offset;
            if (rem > 0)
            {
                std::memcpy(tail, data + offset, rem);
            }
            tail[rem] = 0x80u;
            size_t total_blocks = (rem >= 56) ? 2 : 1;
            uint64_t bit_len = static_cast<uint64_t>(len) * 8ULL;
            size_t len_pos = total_blocks * 64 - 8;
            for (int i = 0; i < 8; ++i)
            {
                tail[len_pos + i] = static_cast<uint8_t>((bit_len >> (56 - i * 8)) & 0xFFu);
            }
            process_block(tail);
            if (total_blocks == 2)
            {
                process_block(tail + 64);
            }
        
            uint32_t hs[5] = { h0, h1, h2, h3, h4 };
            for (int i = 0; i < 5; ++i)
            {
                out_digest[i * 4] = static_cast<uint8_t>((hs[i] >> 24) & 0xFFu);
                out_digest[i * 4 + 1] = static_cast<uint8_t>((hs[i] >> 16) & 0xFFu);
                out_digest[i * 4 + 2] = static_cast<uint8_t>((hs[i] >> 8) & 0xFFu);
                out_digest[i * 4 + 3] = static_cast<uint8_t>(hs[i] & 0xFFu);
            }
        }
        
        static inline void hmac_sha1_8byte_msg(const uint8_t* key, size_t key_len, const uint8_t msg[8], uint8_t out_mac[20]) noexcept
        {
            uint8_t k_block[64]{};
            if (key_len > 64)
            {
                sha1_compute(key, key_len, k_block);
            }
            else if (key_len > 0)
            {
                std::memcpy(k_block, key, key_len);
            }
        
            uint8_t inner_buf[64 + 8];
            uint8_t outer_buf[64 + 20];
            for (int i = 0; i < 64; ++i)
            {
                inner_buf[i] = static_cast<uint8_t>(k_block[i] ^ 0x36u);
                outer_buf[i] = static_cast<uint8_t>(k_block[i] ^ 0x5Cu);
            }
            std::memcpy(inner_buf + 64, msg, 8);
            sha1_compute(inner_buf, sizeof(inner_buf), outer_buf + 64);
            sha1_compute(outer_buf, sizeof(outer_buf), out_mac);
        }
        
        static inline bool is_spotify_id_view(std::u16string_view s) noexcept
        {
            if (s.size() != 22)
            {
                return false;
            }
            for (char16_t ch : s)
            {
                bool ok = (ch >= u'0' && ch <= u'9') || (ch >= u'A' && ch <= u'Z') || (ch >= u'a' && ch <= u'z');
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }
        
        static inline bool contains_whole_phrase_view(std::u16string_view text, std::u16string_view phrase) noexcept
        {
            if (phrase.size() < 3 || text.size() < phrase.size())
            {
                return false;
            }
            size_t start = 0;
            while (start <= text.size() - phrase.size())
            {
                size_t pos = text.find(phrase, start);
                if (pos == std::u16string_view::npos)
                {
                    return false;
                }
                size_t end = pos + phrase.size();
                if ((pos == 0 || text[pos - 1] == u' ') && (end == text.size() || text[end] == u' '))
                {
                    return true;
                }
                start = pos + 1;
            }
            return false;
        }

        static int32_t vn_spotify_generate_totp_impl(const uint8_t* secret_ptr,
    int32_t secret_len,
    int64_t timestamp_ms,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
        {
    if (out_len == nullptr || secret_len < 0 || (secret_len > 0 && secret_ptr == nullptr))
    {
        return VN_ERR_INVALID_ARG;
    }
    int64_t counter = (timestamp_ms / 1000LL) / 30LL;
    uint8_t counter_bytes[8];
    uint64_t ucounter = static_cast<uint64_t>(counter);
    for (int i = 0; i < 8; ++i)
    {
        counter_bytes[i] = static_cast<uint8_t>((ucounter >> (56 - i * 8)) & 0xFFu);
    }

    uint8_t hash[20];
    hmac_sha1_8byte_msg(secret_ptr, static_cast<size_t>(secret_len), counter_bytes, hash);

    int offset = hash[19] & 0x0F;
    int32_t binary = ((hash[offset] & 0x7F) << 24) |
                     ((hash[offset + 1] & 0xFF) << 16) |
                     ((hash[offset + 2] & 0xFF) << 8) |
                     (hash[offset + 3] & 0xFF);
    int32_t code = binary % 1000000;

    char16_t digits[6];
    for (int i = 5; i >= 0; --i)
    {
        digits[i] = static_cast<char16_t>(u'0' + (code % 10));
        code /= 10;
    }
    return copy_u16_out(std::u16string_view(digits, 6), out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_spotify_generate_totp(const uint8_t* secret_ptr,
    int32_t secret_len,
    int64_t timestamp_ms,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    return vnotch::services::SpotifyTokenProvider::vn_spotify_generate_totp_impl(secret_ptr, secret_len, timestamp_ms, out_buf, out_cap, out_len);
}

