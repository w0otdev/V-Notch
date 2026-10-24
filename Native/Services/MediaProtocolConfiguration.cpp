#include "vnotch_common.h"

namespace vnotch::services
{

class MediaProtocolConfiguration final
{
    static constexpr bool IsAsciiHex(char16_t c) noexcept
    {
        return (c >= u'0' && c <= u'9') ||
               (c >= u'a' && c <= u'f') ||
               (c >= u'A' && c <= u'F');
    }

    static constexpr bool IsAsciiAlnum(char16_t c) noexcept
    {
        return vnotch::is_ascii_digit(c) || vnotch::is_ascii_alpha(c);
    }

public:
    static constexpr bool IsHash(std::u16string_view value) noexcept
    {
        if (value.size() != 64)
        {
            return false;
        }
        for (char16_t c : value)
        {
            if (!IsAsciiHex(c))
            {
                return false;
            }
        }
        return true;
    }

    static constexpr bool ValidateScalarFields(
        int32_t schema_version,
        std::u16string_view totp_version,
        int32_t secret_base64_len,
        int32_t secret_decoded_len,
        std::u16string_view find_tracks_hash,
        std::u16string_view canvas_hash,
        std::u16string_view youtube_api_key,
        std::u16string_view youtube_client_version) noexcept
    {
        if (schema_version != 1)
        {
            return false;
        }
        if (totp_version.empty() || totp_version.size() > 8)
        {
            return false;
        }
        for (char16_t c : totp_version)
        {
            if (c < u'0' || c > u'9')
            {
                return false;
            }
        }
        if (secret_base64_len <= 0 || secret_base64_len > 512)
        {
            return false;
        }
        if (secret_decoded_len < 16 || secret_decoded_len > 256)
        {
            return false;
        }
        if (!IsHash(find_tracks_hash) || !IsHash(canvas_hash))
        {
            return false;
        }
        if (youtube_api_key.empty() || youtube_api_key.size() > 256)
        {
            return false;
        }
        for (char16_t c : youtube_api_key)
        {
            if (!IsAsciiAlnum(c) && c != u'_' && c != u'-')
            {
                return false;
            }
        }
        if (youtube_client_version.empty() || youtube_client_version.size() > 32)
        {
            return false;
        }
        for (char16_t c : youtube_client_version)
        {
            if (!vnotch::is_ascii_digit(c) && c != u'.')
            {
                return false;
            }
        }
        return true;
    }

    static bool IsAllowedHostBasic(std::u16string_view host) noexcept
    {
        if (host.empty() || host.size() > 253 || vnotch::is_null_or_whitespace(host))
        {
            return false;
        }
        if (vnotch::trim_view(host).size() != host.size())
        {
            return false;
        }
        if (!vnotch::contains_ordinal(host, u".") || host.back() == u'.')
        {
            return false;
        }
        if (vnotch::ends_with_ignore_case(host, u".localhost") ||
            vnotch::ends_with_ignore_case(host, u".local"))
        {
            return false;
        }
        return true;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_media_protocol_is_hash(
    const char16_t* ptr,
    int32_t len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = (ptr != nullptr && vnotch::services::MediaProtocolConfiguration::IsHash(vnotch::make_u16_view(ptr, len))) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_protocol_validate_scalars(
    int32_t schema_version,
    const char16_t* totp_ver_ptr, int32_t totp_ver_len,
    int32_t secret_b64_len,
    int32_t secret_decoded_len,
    const char16_t* find_hash_ptr, int32_t find_hash_len,
    const char16_t* canvas_hash_ptr, int32_t canvas_hash_len,
    const char16_t* yt_key_ptr, int32_t yt_key_len,
    const char16_t* yt_ver_ptr, int32_t yt_ver_len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    bool ok = totp_ver_ptr != nullptr && find_hash_ptr != nullptr &&
              canvas_hash_ptr != nullptr && yt_key_ptr != nullptr && yt_ver_ptr != nullptr &&
              vnotch::services::MediaProtocolConfiguration::ValidateScalarFields(
                  schema_version,
                  vnotch::make_u16_view(totp_ver_ptr, totp_ver_len),
                  secret_b64_len,
                  secret_decoded_len,
                  vnotch::make_u16_view(find_hash_ptr, find_hash_len),
                  vnotch::make_u16_view(canvas_hash_ptr, canvas_hash_len),
                  vnotch::make_u16_view(yt_key_ptr, yt_key_len),
                  vnotch::make_u16_view(yt_ver_ptr, yt_ver_len));
    *out_valid = ok ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_media_protocol_is_allowed_host_basic(
    const char16_t* host_ptr,
    int32_t host_len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = (host_ptr != nullptr &&
                  vnotch::services::MediaProtocolConfiguration::IsAllowedHostBasic(vnotch::make_u16_view(host_ptr, host_len))) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
