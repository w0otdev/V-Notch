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

    static constexpr void AccumulateCategoryCounts(
        const VnClipboardEntryCategoryMeta* entries,
        int32_t count,
        VnClipboardCategoryCountsOut& out) noexcept {
        out = {};
        for (int32_t i = 0; i < count; ++i) {
            const auto& e = entries[i];
            if (e.is_personal != 0) continue;
            if (e.is_archived != 0) {
                ++out.archive_count;
            } else {
                ++out.all_count;
            }
            if (e.is_pinned != 0) ++out.pin_count;
            if (e.is_archived != 0) continue;
            switch (e.kind) {
                case 0: ++out.text_count; break;
                case 1: ++out.code_count; break;
                case 2: ++out.links_count; break;
                case 3: ++out.images_count; break;
                case 4: ++out.colors_count; break;
                case 5: ++out.files_count; break;
                default: break;
            }
        }
    }

    [[nodiscard]] static constexpr int32_t ComputeReconcileDiff(
        const int32_t* desired_to_existing_index,
        int32_t desired_count,
        uint8_t* out_existing_survivor_mask,
        int32_t existing_count) noexcept {
        for (int32_t m = 0; m < existing_count; ++m) {
            out_existing_survivor_mask[m] = 0;
        }
        int32_t surviving_count = 0;
        int32_t added_count = 0;
        for (int32_t i = 0; i < desired_count; ++i) {
            const int32_t old_idx = desired_to_existing_index[i];
            if (old_idx >= 0 && old_idx < existing_count) {
                if (out_existing_survivor_mask[old_idx] == 0) {
                    out_existing_survivor_mask[old_idx] = 1;
                    ++surviving_count;
                }
            } else {
                ++added_count;
            }
        }
        const int32_t removed_count = existing_count - surviving_count;
        int32_t changes = removed_count + added_count;
        int32_t m_cursor = 0;
        for (int32_t i = 0; i < desired_count; ++i) {
            const int32_t old_idx = desired_to_existing_index[i];
            if (old_idx < 0 || old_idx >= existing_count) continue;
            while (m_cursor < existing_count && out_existing_survivor_mask[m_cursor] == 0) {
                ++m_cursor;
            }
            if (m_cursor != old_idx) {
                ++changes;
            }
            ++m_cursor;
        }
        return changes;
    }

    static constexpr void ComputeAgeBucket(
        int64_t now_utc_ticks,
        int64_t copied_utc_ticks,
        int32_t& out_bucket,
        int32_t& out_value) noexcept {
        constexpr int64_t kTicksPerMinute = 600'000'000LL;
        constexpr int64_t kTicksPerHour = 36'000'000'000LL;
        constexpr int64_t kTicksPerDay = 864'000'000'000LL;
        const int64_t delta = now_utc_ticks - copied_utc_ticks;
        if (delta < kTicksPerMinute) {
            out_bucket = 0;
            out_value = 0;
            return;
        }
        if (delta < kTicksPerHour) {
            out_bucket = 1;
            out_value = static_cast<int32_t>(delta / kTicksPerMinute);
            return;
        }
        if (delta < kTicksPerDay) {
            out_bucket = 2;
            out_value = static_cast<int32_t>(delta / kTicksPerHour);
            return;
        }
        out_bucket = 3;
        out_value = static_cast<int32_t>(delta / kTicksPerDay);
    }

    [[nodiscard]] static bool ValidateArchiveExtractStep(
        int32_t entry_index,
        int32_t max_entries,
        int64_t declared_length,
        int64_t accumulated_bytes,
        int64_t max_total_bytes,
        std::u16string_view root_prefix,
        std::u16string_view resolved_path) noexcept {
        if (entry_index < 0 || max_entries <= 0 || entry_index >= max_entries) {
            return false;
        }
        if (declared_length < 0 || accumulated_bytes < 0 || max_total_bytes <= 0 ||
            accumulated_bytes > max_total_bytes || declared_length > max_total_bytes - accumulated_bytes) {
            return false;
        }
        if (root_prefix.empty() || resolved_path.size() <= root_prefix.size()) {
            return false;
        }
        for (char16_t ch : resolved_path) {
            if (ch == u'\0') return false;
        }
        return vnotch::starts_with_ignore_case(resolved_path, root_prefix);
    }
};

} // namespace vnotch::services::clipboard

extern "C" {

VN_EXPORT int32_t __stdcall vn_clipboard_store_accumulate_category_counts(
    const VnClipboardEntryCategoryMeta* entries,
    int32_t entry_count,
    VnClipboardCategoryCountsOut* out_counts) noexcept {
    if (!out_counts || entry_count < 0 || (entry_count > 0 && !entries)) return VN_E_INVALID_ARG;
    vnotch::services::clipboard::ClipboardHistoryStore::AccumulateCategoryCounts(entries, entry_count, *out_counts);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_tray_compute_reconcile_diff(
    const int32_t* desired_to_existing_index,
    int32_t desired_count,
    uint8_t* out_existing_survivor_mask,
    int32_t existing_count,
    int32_t* out_changes) noexcept {
    if (!out_changes || desired_count < 0 || existing_count < 0 ||
        (desired_count > 0 && !desired_to_existing_index) ||
        (existing_count > 0 && !out_existing_survivor_mask)) {
        return VN_E_INVALID_ARG;
    }
    *out_changes = vnotch::services::clipboard::ClipboardHistoryStore::ComputeReconcileDiff(
        desired_to_existing_index, desired_count, out_existing_survivor_mask, existing_count);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_clipboard_card_compute_age_bucket(
    int64_t now_utc_ticks,
    int64_t copied_utc_ticks,
    int32_t* out_bucket,
    int32_t* out_value) noexcept {
    if (!out_bucket || !out_value) return VN_E_INVALID_ARG;
    vnotch::services::clipboard::ClipboardHistoryStore::ComputeAgeBucket(
        now_utc_ticks, copied_utc_ticks, *out_bucket, *out_value);
    return VN_OK;
}

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

VN_EXPORT int32_t __stdcall vn_clipboard_store_validate_archive_extract_step(
    int32_t entry_index,
    int32_t max_entries,
    int64_t declared_length,
    int64_t accumulated_bytes,
    int64_t max_total_bytes,
    const char16_t* root_prefix,
    int32_t root_prefix_len,
    const char16_t* resolved_path,
    int32_t resolved_path_len,
    uint8_t* out_is_valid) noexcept {
    if (!out_is_valid ||
        (!root_prefix && root_prefix_len > 0) || root_prefix_len < 0 ||
        (!resolved_path && resolved_path_len > 0) || resolved_path_len < 0) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view root_sv(root_prefix ? root_prefix : u"", static_cast<size_t>(root_prefix_len));
    const std::u16string_view path_sv(resolved_path ? resolved_path : u"", static_cast<size_t>(resolved_path_len));
    *out_is_valid = vnotch::services::clipboard::ClipboardHistoryStore::ValidateArchiveExtractStep(
        entry_index, max_entries, declared_length, accumulated_bytes, max_total_bytes, root_sv, path_sv) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
