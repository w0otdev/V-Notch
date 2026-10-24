#include "vnotch_common.h"
#include <cstdint>
#include <cstring>
#include <string_view>

namespace vnotch::services::translation {

class CustomTranslationModels final {
public:
    CustomTranslationModels() = delete;

    [[nodiscard]] static constexpr bool IsValidCustomModelId(std::u16string_view id) noexcept {
        constexpr std::u16string_view kPrefix = u"custom-";
        if (id.size() != 39 || id.substr(0, kPrefix.size()) != kPrefix) return false;
        for (size_t i = kPrefix.size(); i < 39; ++i) {
            const char16_t ch = id[i];
            const bool is_lower_hex = (ch >= u'0' && ch <= u'9') || (ch >= u'a' && ch <= u'f');
            if (!is_lower_hex) return false;
        }
        return true;
    }

    [[nodiscard]] static constexpr bool IsValidSha256Hex(std::u16string_view hash) noexcept {
        if (hash.size() != 64) return false;
        for (const char16_t ch : hash) {
            if (!is_ascii_hex(ch)) return false;
        }
        return true;
    }

    // Matches @"-\d{5}-of-\d{5}\.gguf$" (IgnoreCase)
    [[nodiscard]] static constexpr bool IsSplitGgufFileName(std::u16string_view file_name) noexcept {
        if (file_name.size() < 20) return false;
        const std::u16string_view tail = file_name.substr(file_name.size() - 20);
        if (tail[0] != u'-') return false;
        for (size_t i = 1; i <= 5; ++i) {
            if (!is_ascii_digit(tail[i])) return false;
        }
        if (!vn_equals_icase_ascii(tail.substr(6, 4), u"-of-")) return false;
        for (size_t i = 10; i <= 14; ++i) {
            if (!is_ascii_digit(tail[i])) return false;
        }
        return vn_equals_icase_ascii(tail.substr(15, 5), u".gguf");
    }

    [[nodiscard]] static bool ValidateGgufHeader(const uint8_t* header, int32_t header_len) noexcept {
        if (!header || header_len < 24) return false;
        if (header[0] != 'G' || header[1] != 'G' || header[2] != 'U' || header[3] != 'F') return false;
        uint32_t version = 0;
        std::memcpy(&version, header + 4, sizeof(uint32_t));
        if (version != 2U && version != 3U) return false;
        uint64_t tensor_count = 0;
        std::memcpy(&tensor_count, header + 8, sizeof(uint64_t));
        return tensor_count != 0ULL;
    }
};

} // namespace vnotch::services::translation

extern "C" {

VN_EXPORT int32_t vn_custom_translation_models_is_valid_id(
    const char16_t* id,
    int32_t id_len,
    uint8_t* out_valid) noexcept {
    if (!out_valid || (!id && id_len > 0) || id_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(id ? id : u"", static_cast<size_t>(id_len));
    *out_valid = vnotch::services::translation::CustomTranslationModels::IsValidCustomModelId(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_custom_translation_models_is_valid_sha256_hex(
    const char16_t* hash,
    int32_t hash_len,
    uint8_t* out_valid) noexcept {
    if (!out_valid || (!hash && hash_len > 0) || hash_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(hash ? hash : u"", static_cast<size_t>(hash_len));
    *out_valid = vnotch::services::translation::CustomTranslationModels::IsValidSha256Hex(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_custom_translation_models_is_split_gguf_file_name(
    const char16_t* file_name,
    int32_t file_name_len,
    uint8_t* out_is_split) noexcept {
    if (!out_is_split || (!file_name && file_name_len > 0) || file_name_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(file_name ? file_name : u"", static_cast<size_t>(file_name_len));
    *out_is_split = vnotch::services::translation::CustomTranslationModels::IsSplitGgufFileName(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_custom_translation_models_validate_gguf_header(
    const uint8_t* header,
    int32_t header_len,
    uint8_t* out_valid) noexcept {
    if (!out_valid || (!header && header_len > 0) || header_len < 0) return VN_E_INVALID_ARG;
    *out_valid = vnotch::services::translation::CustomTranslationModels::ValidateGgufHeader(header, header_len) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
