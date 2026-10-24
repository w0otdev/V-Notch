#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <string_view>

namespace vnotch::services::spotlight::providers {

class EverythingSearchProvider final {
public:
    EverythingSearchProvider() = delete;

    static constexpr int32_t kMaxParsedItems = 512;
    static constexpr uint32_t kMatchPath = 0x00000004U;

    [[nodiscard]] static constexpr void ComputeSearchParams(
        std::u16string_view query,
        int32_t limit,
        int32_t& out_max_results,
        uint32_t& out_search_flags) noexcept {
        out_max_results = std::clamp(limit * 8, 100, kMaxParsedItems);
        bool has_slash = false;
        for (const char16_t ch : query) {
            if (ch == u'\\' || ch == u'/') {
                has_slash = true;
                break;
            }
        }
        out_search_flags = has_slash ? kMatchPath : 0U;
    }

    [[nodiscard]] static constexpr bool IsExecutableExtension(std::u16string_view ext) noexcept {
        return vn_equals_icase_ascii(ext, u".exe") ||
               vn_equals_icase_ascii(ext, u".cmd") ||
               vn_equals_icase_ascii(ext, u".bat") ||
               vn_equals_icase_ascii(ext, u".msc") ||
               vn_equals_icase_ascii(ext, u".cpl") ||
               vn_equals_icase_ascii(ext, u".com") ||
               vn_equals_icase_ascii(ext, u".ps1") ||
               vn_equals_icase_ascii(ext, u".appref-ms") ||
               vn_equals_icase_ascii(ext, u".lnk");
    }

    // SpotlightResultKind: Application=0, File=3, Folder=4
    [[nodiscard]] static constexpr int32_t ClassifyItemKind(
        bool is_folder,
        std::u16string_view ext) noexcept {
        if (is_folder) return 4;
        if (IsExecutableExtension(ext)) return 0;
        return 3;
    }

    [[nodiscard]] static constexpr double ComputeFinalScore(
        double ranker_score,
        int32_t title_len) noexcept {
        if (ranker_score > 0.0) return ranker_score;
        return static_cast<double>(std::max(120, 260 - title_len));
    }

    [[nodiscard]] static int32_t ParseBoundedUtf16Length(
        const uint8_t* base_ptr,
        int32_t offset,
        int32_t total_bytes) noexcept {
        if (!base_ptr || offset < 0 || offset >= total_bytes) return 0;
        const int32_t max_chars = (total_bytes - offset) / static_cast<int32_t>(sizeof(char16_t));
        if (max_chars <= 0) return 0;
        const auto* chars = reinterpret_cast<const char16_t*>(base_ptr + offset);
        for (int32_t i = 0; i < max_chars; ++i) {
            if (chars[i] == u'\0') return i;
        }
        return max_chars;
    }
};

} // namespace vnotch::services::spotlight::providers

extern "C" {

VN_EXPORT int32_t vn_everything_provider_compute_search_params(
    const char16_t* query,
    int32_t query_len,
    int32_t limit,
    int32_t* out_max_results,
    uint32_t* out_search_flags) noexcept {
    if (!out_max_results || !out_search_flags || (!query && query_len > 0) || query_len < 0) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view q(query ? query : u"", static_cast<size_t>(query_len));
    int32_t max_res = 0;
    uint32_t flags = 0;
    vnotch::services::spotlight::providers::EverythingSearchProvider::ComputeSearchParams(q, limit, max_res, flags);
    *out_max_results = max_res;
    *out_search_flags = flags;
    return VN_OK;
}

VN_EXPORT int32_t vn_everything_provider_classify_item_kind(
    uint8_t is_folder,
    const char16_t* ext,
    int32_t ext_len,
    int32_t* out_kind) noexcept {
    if (!out_kind || (!ext && ext_len > 0) || ext_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(ext ? ext : u"", static_cast<size_t>(ext_len));
    *out_kind = vnotch::services::spotlight::providers::EverythingSearchProvider::ClassifyItemKind(is_folder != 0, sv);
    return VN_OK;
}

VN_EXPORT int32_t vn_everything_provider_compute_final_score(
    double ranker_score,
    int32_t title_len,
    double* out_score) noexcept {
    if (!out_score) return VN_E_INVALID_ARG;
    *out_score = vnotch::services::spotlight::providers::EverythingSearchProvider::ComputeFinalScore(ranker_score, title_len);
    return VN_OK;
}

VN_EXPORT int32_t vn_everything_provider_parse_bounded_utf16_len(
    const uint8_t* base_ptr,
    int32_t offset,
    int32_t total_bytes,
    int32_t* out_char_len) noexcept {
    if (!out_char_len) return VN_E_INVALID_ARG;
    *out_char_len = vnotch::services::spotlight::providers::EverythingSearchProvider::ParseBoundedUtf16Length(
        base_ptr, offset, total_bytes);
    return VN_OK;
}

} // extern "C"
