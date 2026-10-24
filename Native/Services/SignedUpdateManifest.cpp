#include "../vnotch_common.h"
#include <string_view>

namespace vnotch::services {

class SignedUpdateManifest final {
public:
    static constexpr int32_t MaximumManifestBytes = 16 * 1024;

    static bool IsPayloadAndSignatureSizeValid(int32_t payload_length, int32_t signature_length) noexcept {
        return payload_length > 0 && payload_length <= MaximumManifestBytes && signature_length == 64;
    }

    static bool IsValidSha256Hex(std::u16string_view sha256) noexcept {
        if (sha256.size() != 64) return false;
        for (char16_t ch : sha256) {
            const bool is_digit = (ch >= u'0' && ch <= u'9');
            const bool is_lower_hex = (ch >= u'a' && ch <= u'f');
            const bool is_upper_hex = (ch >= u'A' && ch <= u'F');
            if (!is_digit && !is_lower_hex && !is_upper_hex) {
                return false;
            }
        }
        return true;
    }

    static bool IsManifestMetadataValid(
        int32_t schema_version,
        int64_t size,
        int64_t max_installer_bytes,
        std::u16string_view sha256) noexcept {
        if (schema_version != 1) return false;
        if (size <= 0 || size > max_installer_bytes) return false;
        return IsValidSha256Hex(sha256);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_signed_update_manifest_validate_sizes(
    int32_t payload_length,
    int32_t signature_length,
    uint8_t* out_valid) noexcept {
    if (!out_valid) return VN_E_INVALID_ARG;
    *out_valid = vnotch::services::SignedUpdateManifest::IsPayloadAndSignatureSizeValid(
        payload_length, signature_length) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_signed_update_manifest_validate_metadata(
    int32_t schema_version,
    int64_t size,
    int64_t max_installer_bytes,
    const char16_t* sha256_ptr,
    int32_t sha256_len,
    uint8_t* out_valid) noexcept {
    if (!out_valid) return VN_E_INVALID_ARG;
    std::u16string_view sha256{};
    if (sha256_ptr && sha256_len > 0) {
        sha256 = std::u16string_view(sha256_ptr, static_cast<size_t>(sha256_len));
    }
    *out_valid = vnotch::services::SignedUpdateManifest::IsManifestMetadataValid(
        schema_version, size, max_installer_bytes, sha256) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
