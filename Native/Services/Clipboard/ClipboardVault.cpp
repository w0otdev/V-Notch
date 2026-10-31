#include "vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <string_view>

namespace vnotch::services::clipboard {

class ClipboardVault final {
public:
    ClipboardVault() = delete;

    [[nodiscard]] static double ComputeLockoutBackoffSeconds(int32_t failed_attempts) noexcept {
        const int32_t exp_val = std::min(6, failed_attempts - 1);
        return std::min(60.0, std::pow(2.0, static_cast<double>(exp_val)));
    }

    // 1 = "enc:", 2 = "raw:", 0 = invalid
    [[nodiscard]] static constexpr int32_t ClassifyPepperPrefix(std::u16string_view pepper) noexcept {
        if (pepper.size() >= 4) {
            if (pepper.substr(0, 4) == u"enc:") return 1;
            if (pepper.substr(0, 4) == u"raw:") return 2;
        }
        return 0;
    }

    [[nodiscard]] static constexpr bool ValidateEncryptedAttachmentLength(
        int64_t plain_length,
        int64_t stream_length,
        int32_t chunk_size) noexcept {
        if (chunk_size <= 0 || plain_length < 0 || stream_length < 44LL || plain_length > stream_length - 44LL) {
            return false;
        }
        const int64_t cs = static_cast<int64_t>(chunk_size);
        const int64_t chunks = (plain_length == 0LL) ? 1LL : ((plain_length - 1LL) / cs + 1LL);
        const int64_t max_tag_bytes = stream_length - 16LL - plain_length;
        if (chunks > max_tag_bytes / 28LL) {
            return false;
        }
        const int64_t expected_stream_length = 16LL + plain_length + chunks * 28LL;
        return stream_length == expected_stream_length;
    }
};

} // namespace vnotch::services::clipboard

extern "C" {

VN_EXPORT int32_t vn_clipboard_vault_compute_lockout_backoff_seconds(
    int32_t failed_attempts,
    double* out_seconds) noexcept {
    if (!out_seconds) return VN_E_INVALID_ARG;
    *out_seconds = vnotch::services::clipboard::ClipboardVault::ComputeLockoutBackoffSeconds(failed_attempts);
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_vault_classify_pepper_prefix(
    const char16_t* pepper,
    int32_t pepper_len,
    int32_t* out_prefix_type) noexcept {
    if (!out_prefix_type || (!pepper && pepper_len > 0) || pepper_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(pepper ? pepper : u"", static_cast<size_t>(pepper_len));
    *out_prefix_type = vnotch::services::clipboard::ClipboardVault::ClassifyPepperPrefix(sv);
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_vault_validate_encrypted_attachment_length(
    int64_t plain_length,
    int64_t stream_length,
    int32_t chunk_size,
    uint8_t* out_valid) noexcept {
    if (!out_valid) return VN_E_INVALID_ARG;
    *out_valid = vnotch::services::clipboard::ClipboardVault::ValidateEncryptedAttachmentLength(
        plain_length, stream_length, chunk_size) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
