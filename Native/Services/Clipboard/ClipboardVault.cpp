#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services::clipboard {

class ClipboardVault final {
public:
    ClipboardVault() = delete;

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
