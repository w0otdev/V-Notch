#include "vnotch_common.h"
#include <cstdint>
#include <string_view>

namespace vnotch::services::clipboard {

class ClipboardHistoryStore final {
public:
    ClipboardHistoryStore() = delete;

    // ClipboardKind: Text=0, Code=1, Link=2, Image=3, Color=4, File=5
    // Returns: 1 = matches, 0 = does not match, -1 = check custom Groups (when include_archived || !is_archived)
    [[nodiscard]] static constexpr int32_t MatchesCategoryCore(
        bool is_personal,
        bool is_archived,
        bool is_pinned,
        int32_t kind,
        bool include_archived,
        std::u16string_view category) noexcept {
        if (category.empty() || vn_equals_icase_ascii(category, u"all")) {
            return (!is_personal && !is_archived) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"archive")) {
            return (is_archived && !is_personal) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"pin") || vn_equals_icase_ascii(category, u"pinned")) {
            return (is_pinned && !is_personal) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"personal")) {
            return is_personal ? 1 : 0;
        }
        const bool archive_ok = include_archived || !is_archived;
        if (vn_equals_icase_ascii(category, u"text")) {
            return (archive_ok && kind == 0) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"code")) {
            return (archive_ok && kind == 1) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"link") || vn_equals_icase_ascii(category, u"links")) {
            return (archive_ok && kind == 2) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"image") || vn_equals_icase_ascii(category, u"images")) {
            return (archive_ok && kind == 3) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"color") || vn_equals_icase_ascii(category, u"colors")) {
            return (archive_ok && kind == 4) ? 1 : 0;
        }
        if (vn_equals_icase_ascii(category, u"file") || vn_equals_icase_ascii(category, u"files")) {
            return (archive_ok && kind == 5) ? 1 : 0;
        }
        return archive_ok ? -1 : 0;
    }

    [[nodiscard]] static constexpr bool IsScreenshotFileName(std::u16string_view name) noexcept {
        return !name.empty() && name.front() == u'{' && vn_ends_with_icase_ascii(name, u"}.png");
    }

    [[nodiscard]] static constexpr bool IsScreenshotSourceApp(std::u16string_view source_app) noexcept {
        return vn_equals_icase_ascii(source_app, u"svchost") ||
               vn_equals_icase_ascii(source_app, u"SnippingTool") ||
               vn_equals_icase_ascii(source_app, u"ScreenClippingHost");
    }

    [[nodiscard]] static constexpr bool IsSharingViolationHResult(int32_t hresult) noexcept {
        const int32_t code = hresult & 0xFFFF;
        return code == 32 || code == 33;
    }
};

} // namespace vnotch::services::clipboard

extern "C" {

VN_EXPORT int32_t vn_clipboard_store_matches_category_core(
    uint8_t is_personal,
    uint8_t is_archived,
    uint8_t is_pinned,
    int32_t kind,
    uint8_t include_archived,
    const char16_t* category,
    int32_t category_len,
    int32_t* out_result) noexcept {
    if (!out_result || (!category && category_len > 0) || category_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view cat(category ? category : u"", static_cast<size_t>(category_len));
    *out_result = vnotch::services::clipboard::ClipboardHistoryStore::MatchesCategoryCore(
        is_personal != 0, is_archived != 0, is_pinned != 0, kind, include_archived != 0, cat);
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_store_is_screenshot_file_name(
    const char16_t* name,
    int32_t name_len,
    uint8_t* out_is_screenshot) noexcept {
    if (!out_is_screenshot || (!name && name_len > 0) || name_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(name ? name : u"", static_cast<size_t>(name_len));
    *out_is_screenshot = vnotch::services::clipboard::ClipboardHistoryStore::IsScreenshotFileName(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_store_is_screenshot_source_app(
    const char16_t* source_app,
    int32_t source_app_len,
    uint8_t* out_is_screenshot_app) noexcept {
    if (!out_is_screenshot_app || (!source_app && source_app_len > 0) || source_app_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(source_app ? source_app : u"", static_cast<size_t>(source_app_len));
    *out_is_screenshot_app = vnotch::services::clipboard::ClipboardHistoryStore::IsScreenshotSourceApp(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_clipboard_store_is_sharing_violation_hresult(
    int32_t hresult,
    uint8_t* out_is_sharing_violation) noexcept {
    if (!out_is_sharing_violation) return VN_E_INVALID_ARG;
    *out_is_sharing_violation = vnotch::services::clipboard::ClipboardHistoryStore::IsSharingViolationHResult(hresult) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
