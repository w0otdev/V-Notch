#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string_view>

namespace vnotch::services::spotlight::providers {

class EverythingSearchProvider final {
public:
    EverythingSearchProvider() = delete;

    static constexpr int32_t kMaxParsedItems = 512;
    static constexpr uint32_t kMatchPath = 0x00000004U;
    static constexpr int32_t kListHeaderBytes = 28;
    static constexpr int32_t kListNumItemsOffset = 20;
    static constexpr int32_t kItemBytes = 12;
    static constexpr int32_t kItemFolderFlag = 0x00000001;
    static constexpr int32_t kQueryHeaderBytes = 20;

    static constexpr void ComputeSearchParams(
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

    // SpotlightResultKind: Application=0, File=1, Folder=2
    [[nodiscard]] static constexpr int32_t ClassifyItemKind(
        bool is_folder,
        std::u16string_view ext) noexcept {
        if (is_folder) return 2;
        if (IsExecutableExtension(ext)) return 0;
        return 1;
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

    [[nodiscard]] static int32_t ReadReplyItemCount(
        const uint8_t* list_ptr,
        int32_t byte_count) noexcept {
        if (!list_ptr || byte_count < kListHeaderBytes) {
            return 0;
        }
        int32_t raw_count = 0;
        std::memcpy(&raw_count, list_ptr + kListNumItemsOffset, sizeof(int32_t));
        const int32_t clamped = std::clamp(raw_count, 0, kMaxParsedItems);
        const int32_t max_by_bytes = (byte_count - kListHeaderBytes) / kItemBytes;
        return std::min(clamped, max_by_bytes);
    }

    static void ExtractReplyItem(
        const uint8_t* list_ptr,
        int32_t byte_count,
        int32_t index,
        int32_t& out_name_offset,
        int32_t& out_name_len,
        int32_t& out_path_offset,
        int32_t& out_path_len,
        uint8_t& out_is_folder) noexcept {
        out_name_offset = 0;
        out_name_len = 0;
        out_path_offset = 0;
        out_path_len = 0;
        out_is_folder = 0;

        if (!list_ptr || index < 0 || index >= kMaxParsedItems) {
            return;
        }
        const int32_t item_offset = kListHeaderBytes + index * kItemBytes;
        if (item_offset < kListHeaderBytes || item_offset + kItemBytes > byte_count) {
            return;
        }

        int32_t flags = 0;
        int32_t name_offset = 0;
        int32_t path_offset = 0;
        std::memcpy(&flags, list_ptr + item_offset, sizeof(int32_t));
        std::memcpy(&name_offset, list_ptr + item_offset + 4, sizeof(int32_t));
        std::memcpy(&path_offset, list_ptr + item_offset + 8, sizeof(int32_t));

        if (name_offset < 0 || name_offset >= byte_count) return;
        if (path_offset < 0 || path_offset >= byte_count) return;

        const int32_t name_len = ParseBoundedUtf16Length(list_ptr, name_offset, byte_count);
        if (name_len <= 0) return;
        const int32_t path_len = ParseBoundedUtf16Length(list_ptr, path_offset, byte_count);

        out_name_offset = name_offset;
        out_name_len = name_len;
        out_path_offset = path_offset;
        out_path_len = path_len;
        out_is_folder = (flags & kItemFolderFlag) != 0 ? 1u : 0u;
    }

    static int32_t PackIpcQuery(
        int64_t reply_hwnd,
        uint32_t query_id,
        uint32_t search_flags,
        int32_t max_results,
        std::u16string_view query,
        uint8_t* out_buf,
        int32_t out_cap,
        int32_t* out_bytes) noexcept {
        if (!out_bytes) return VN_ERR_INVALID_ARG;
        const int32_t string_bytes = static_cast<int32_t>((query.size() + 1) * sizeof(char16_t));
        const int32_t total_bytes = kQueryHeaderBytes + string_bytes;
        *out_bytes = total_bytes;
        if (!out_buf || out_cap < total_bytes) {
            return VN_ERR_BUFFER_TOO_SMALL;
        }

        const int32_t hwnd32 = static_cast<int32_t>(static_cast<uint32_t>(reply_hwnd));
        const int32_t qid32 = static_cast<int32_t>(query_id);
        const int32_t flags32 = static_cast<int32_t>(search_flags);
        const int32_t offset32 = 0;

        std::memcpy(out_buf + 0, &hwnd32, sizeof(int32_t));
        std::memcpy(out_buf + 4, &qid32, sizeof(int32_t));
        std::memcpy(out_buf + 8, &flags32, sizeof(int32_t));
        std::memcpy(out_buf + 12, &offset32, sizeof(int32_t));
        std::memcpy(out_buf + 16, &max_results, sizeof(int32_t));

        if (!query.empty()) {
            std::memcpy(out_buf + kQueryHeaderBytes, query.data(), query.size() * sizeof(char16_t));
        }
        const char16_t nul = u'\0';
        std::memcpy(out_buf + kQueryHeaderBytes + query.size() * sizeof(char16_t), &nul, sizeof(char16_t));
        return VN_OK;
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

VN_EXPORT int32_t __stdcall vn_everything_provider_read_reply_item_count(
    const uint8_t* list_ptr,
    int32_t byte_count,
    int32_t* out_count) noexcept {
    if (!out_count) return VN_ERR_INVALID_ARG;
    *out_count = vnotch::services::spotlight::providers::EverythingSearchProvider::ReadReplyItemCount(
        list_ptr, byte_count);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_everything_provider_extract_reply_item(
    const uint8_t* list_ptr,
    int32_t byte_count,
    int32_t index,
    int32_t* out_name_offset,
    int32_t* out_name_len,
    int32_t* out_path_offset,
    int32_t* out_path_len,
    uint8_t* out_is_folder) noexcept {
    if (!out_name_offset || !out_name_len || !out_path_offset || !out_path_len || !out_is_folder) {
        return VN_ERR_INVALID_ARG;
    }
    vnotch::services::spotlight::providers::EverythingSearchProvider::ExtractReplyItem(
        list_ptr,
        byte_count,
        index,
        *out_name_offset,
        *out_name_len,
        *out_path_offset,
        *out_path_len,
        *out_is_folder);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_everything_provider_pack_ipc_query(
    int64_t reply_hwnd,
    uint32_t query_id,
    uint32_t search_flags,
    int32_t max_results,
    const char16_t* query_ptr,
    int32_t query_len,
    uint8_t* out_buf,
    int32_t out_cap,
    int32_t* out_bytes) noexcept {
    return vnotch::services::spotlight::providers::EverythingSearchProvider::PackIpcQuery(
        reply_hwnd,
        query_id,
        search_flags,
        max_results,
        vnotch::make_u16_view(query_ptr, query_len),
        out_buf,
        out_cap,
        out_bytes);
}

} // extern "C"
