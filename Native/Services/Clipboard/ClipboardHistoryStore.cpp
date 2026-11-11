#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <limits>
#include <string_view>

#ifndef VN_CLIPBOARD_PRUNE_CANDIDATE_DEFINED
#define VN_CLIPBOARD_PRUNE_CANDIDATE_DEFINED
#pragma pack(push, 1)
struct VnClipboardPruneCandidate {
    int32_t entry_index;
    uint8_t is_personal;
    uint8_t is_pinned;
    uint8_t is_archived;
    int64_t copied_utc_ticks;
    int64_t byte_size;
};
#pragma pack(pop)
#endif

namespace vnotch::services::clipboard {

class ClipboardHistoryStore final {
public:
    ClipboardHistoryStore() = delete;

    [[nodiscard]] static inline bool EqualsOrdinalIgnoreCase(
        std::u16string_view a,
        std::u16string_view b) noexcept {
        if (a.size() != b.size()) return false;
        if (a.empty()) return true;
        bool has_non_ascii = false;
        for (size_t i = 0; i < a.size(); ++i) {
            char16_t ca = a[i];
            char16_t cb = b[i];
            if ((ca | cb) >= 0x80) {
                has_non_ascii = true;
                break;
            }
            if (ca >= u'A' && ca <= u'Z') ca = static_cast<char16_t>(ca + 32);
            if (cb >= u'A' && cb <= u'Z') cb = static_cast<char16_t>(cb + 32);
            if (ca != cb) return false;
        }
        if (!has_non_ascii) return true;
        return CompareStringOrdinal(
                   reinterpret_cast<LPCWCH>(a.data()),
                   static_cast<int>(a.size()),
                   reinterpret_cast<LPCWCH>(b.data()),
                   static_cast<int>(b.size()),
                   TRUE) == CSTR_EQUAL;
    }

    [[nodiscard]] static inline bool ContainsOrdinalIgnoreCase(
        std::u16string_view haystack,
        std::u16string_view needle) noexcept {
        if (needle.empty()) return true;
        if (haystack.size() < needle.size()) return false;
        bool has_non_ascii = false;
        for (char16_t ch : needle) {
            if (ch >= 0x80) {
                has_non_ascii = true;
                break;
            }
        }
        const size_t limit = haystack.size() - needle.size();
        if (!has_non_ascii) {
            const char16_t first_lo = (needle[0] >= u'A' && needle[0] <= u'Z')
                ? static_cast<char16_t>(needle[0] + 32)
                : needle[0];
            const char16_t first_hi = (needle[0] >= u'a' && needle[0] <= u'z')
                ? static_cast<char16_t>(needle[0] - 32)
                : needle[0];
            for (size_t i = 0; i <= limit; ++i) {
                const char16_t c = haystack[i];
                if (c != first_lo && c != first_hi) continue;
                bool match = true;
                for (size_t j = 1; j < needle.size(); ++j) {
                    char16_t hc = haystack[i + j];
                    char16_t nc = needle[j];
                    if (hc >= u'A' && hc <= u'Z') hc = static_cast<char16_t>(hc + 32);
                    if (nc >= u'A' && nc <= u'Z') nc = static_cast<char16_t>(nc + 32);
                    if (hc != nc) {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }
        for (size_t i = 0; i <= limit; ++i) {
            if (CompareStringOrdinal(
                    reinterpret_cast<LPCWCH>(haystack.data() + i),
                    static_cast<int>(needle.size()),
                    reinterpret_cast<LPCWCH>(needle.data()),
                    static_cast<int>(needle.size()),
                    TRUE) == CSTR_EQUAL) {
                return true;
            }
        }
        return false;
    }

    static int32_t TokenizeQuery(
        std::u16string_view query,
        int32_t* out_starts,
        int32_t* out_lengths,
        int32_t max_tokens) noexcept {
        int32_t count = 0;
        size_t i = 0;
        const size_t n = query.size();
        while (i < n && count < max_tokens) {
            while (i < n && query[i] == u' ') {
                ++i;
            }
            if (i >= n) break;
            size_t end = i;
            while (end < n && query[end] != u' ') {
                ++end;
            }
            size_t tok_start = i;
            size_t tok_end = end;
            while (tok_start < tok_end && vnotch::is_unicode_whitespace(query[tok_start])) {
                ++tok_start;
            }
            while (tok_end > tok_start && vnotch::is_unicode_whitespace(query[tok_end - 1])) {
                --tok_end;
            }
            if (tok_end > tok_start) {
                out_starts[count] = static_cast<int32_t>(tok_start);
                out_lengths[count] = static_cast<int32_t>(tok_end - tok_start);
                ++count;
            }
            i = end + 1;
        }
        return count;
    }

    [[nodiscard]] static bool EntryMatchesMetadataToken(
        bool is_sensitive,
        std::u16string_view title,
        std::u16string_view preview,
        std::u16string_view source_app,
        std::u16string_view token) noexcept {
        if (!is_sensitive) {
            if (ContainsOrdinalIgnoreCase(title, token)) return true;
            if (ContainsOrdinalIgnoreCase(preview, token)) return true;
        }
        return ContainsOrdinalIgnoreCase(source_app, token);
    }

    static void PlanPrune(
        const VnClipboardPruneCandidate* candidates,
        int32_t candidate_count,
        int64_t cutoff_utc_ticks,
        int32_t max_unpinned_entries,
        int64_t max_unpinned_bytes,
        int32_t* out_ordered_indices,
        int32_t max_indices,
        int32_t& out_expired_count,
        int32_t& out_evict_candidate_count,
        int64_t& out_total_active_bytes) noexcept {
        out_expired_count = 0;
        out_evict_candidate_count = 0;
        out_total_active_bytes = 0;
        if (candidate_count <= 0 || max_indices <= 0) return;

        const int32_t capped_max_entries = std::max<int32_t>(0, max_unpinned_entries);
        const int64_t capped_max_bytes = std::max<int64_t>(0, max_unpinned_bytes);

        int32_t expired_count = 0;
        int32_t active_count = 0;
        int64_t active_bytes = 0;

        for (int32_t i = 0; i < candidate_count; ++i) {
            const auto& c = candidates[i];
            if (c.is_personal != 0 || c.is_pinned != 0 || c.is_archived != 0) continue;
            if (c.copied_utc_ticks < cutoff_utc_ticks) {
                ++expired_count;
            } else {
                ++active_count;
                const int64_t sz = std::max<int64_t>(0, c.byte_size);
                if (active_bytes > std::numeric_limits<int64_t>::max() - sz) {
                    active_bytes = std::numeric_limits<int64_t>::max();
                } else {
                    active_bytes += sz;
                }
            }
        }

        out_total_active_bytes = active_bytes;
        if (expired_count == 0 && active_count <= capped_max_entries && active_bytes <= capped_max_bytes) {
            return;
        }

        int32_t exp_write = 0;
        int32_t act_write = std::min<int32_t>(expired_count, max_indices);
        const int32_t act_start = act_write;

        for (int32_t i = 0; i < candidate_count; ++i) {
            const auto& c = candidates[i];
            if (c.is_personal != 0 || c.is_pinned != 0 || c.is_archived != 0) continue;
            if (c.copied_utc_ticks < cutoff_utc_ticks) {
                if (exp_write < act_start) {
                    out_ordered_indices[exp_write++] = c.entry_index;
                }
            } else {
                if (act_write < max_indices) {
                    out_ordered_indices[act_write++] = i;
                }
            }
        }

        const int32_t actual_act_count = act_write - act_start;
        int32_t* act_slice = out_ordered_indices + act_start;
        std::sort(act_slice, act_slice + actual_act_count, [candidates](int32_t a, int32_t b) noexcept {
            const int64_t ta = candidates[a].copied_utc_ticks;
            const int64_t tb = candidates[b].copied_utc_ticks;
            return (ta != tb) ? (ta < tb) : (a < b);
        });
        for (int32_t k = 0; k < actual_act_count; ++k) {
            act_slice[k] = candidates[act_slice[k]].entry_index;
        }

        out_expired_count = exp_write;
        out_evict_candidate_count = actual_act_count;
    }

    [[nodiscard]] static constexpr int32_t EvaluateSensitiveExpiry(
        bool has_entry,
        bool is_pinned,
        bool is_archived,
        bool is_personal,
        bool has_personal_dir,
        bool ticks_valid,
        int64_t marker_ticks,
        int64_t expiry_cutoff_ticks) noexcept {
        if ((has_entry && (is_pinned || is_archived || is_personal)) || has_personal_dir) {
            return 1; // Protected: remove marker only
        }
        if (!ticks_valid || marker_ticks > expiry_cutoff_ticks) {
            return 0; // Keep
        }
        return 2; // Expired: delete directory, marker, and entry
    }

    [[nodiscard]] static constexpr bool IsVaultStreamMagic(const uint8_t* magic, int32_t len) noexcept {
        if (!magic || len < 4) return false;
        return (magic[0] == 'V' && magic[1] == 'N' && magic[2] == 'C' && magic[3] == '1') ||
               (magic[0] == 'V' && magic[1] == 'C' && magic[2] == 'F' && magic[3] == '1');
    }

    [[nodiscard]] static constexpr bool FormatPrefixedSha256Hex(
        std::u16string_view prefix,
        const uint8_t* hash_bytes,
        int32_t hash_len,
        char16_t* out_buf,
        int32_t out_cap,
        int32_t& out_written) noexcept {
        out_written = 0;
        if (!out_buf || prefix.size() > 1024 || hash_len < 0 || hash_len > 1024 || out_cap <= 0 || (hash_len > 0 && !hash_bytes)) return false;
        const size_t total_len = prefix.size() + static_cast<size_t>(hash_len) * 2u;
        if (static_cast<size_t>(out_cap) < total_len) return false;
        static constexpr char16_t kHexDigits[16] = {
            u'0', u'1', u'2', u'3', u'4', u'5', u'6', u'7',
            u'8', u'9', u'A', u'B', u'C', u'D', u'E', u'F'
        };
        int32_t pos = 0;
        for (char16_t ch : prefix) {
            out_buf[pos++] = ch;
        }
        for (int32_t i = 0; i < hash_len; ++i) {
            const uint8_t b = hash_bytes[i];
            out_buf[pos++] = kHexDigits[(b >> 4) & 0x0F];
            out_buf[pos++] = kHexDigits[b & 0x0F];
        }
        out_written = pos;
        return true;
    }

    [[nodiscard]] static constexpr bool AreSignaturesEqual(
        std::u16string_view a,
        std::u16string_view b) noexcept {
        if (a.size() != b.size()) return false;
        uint16_t diff = 0;
        for (size_t i = 0; i < a.size(); ++i) {
            diff |= static_cast<uint16_t>(a[i] ^ b[i]);
        }
        return diff == 0;
    }

    [[nodiscard]] static constexpr bool IsProbableDuplicateImageCandidate(
        int32_t kind,
        bool is_personal,
        int64_t capture_ticks,
        int64_t entry_ticks,
        int64_t best_ticks,
        bool has_best) noexcept {
        constexpr int64_t kTwoSecondsTicks = 20'000'000LL;
        if (kind != 3 || is_personal) return false;
        const int64_t diff = (capture_ticks >= entry_ticks)
            ? (capture_ticks - entry_ticks)
            : (entry_ticks - capture_ticks);
        if (diff >= kTwoSecondsTicks) return false;
        return !has_best || entry_ticks > best_ticks;
    }

    [[nodiscard]] static bool IsExportPathProtectedByLease(
        std::u16string_view directory,
        std::u16string_view active_lease,
        char16_t dir_sep) noexcept {
        if (EqualsOrdinalIgnoreCase(directory, active_lease)) return true;
        if (active_lease.size() > directory.size() &&
             (active_lease[directory.size()] == dir_sep || active_lease[directory.size()] == u'\\' || active_lease[directory.size()] == u'/') &&
            EqualsOrdinalIgnoreCase(active_lease.substr(0, directory.size()), directory)) {
            return true;
        }
        if (directory.size() > active_lease.size() &&
            (directory[active_lease.size()] == dir_sep || directory[active_lease.size()] == u'\\' || directory[active_lease.size()] == u'/') &&
            EqualsOrdinalIgnoreCase(directory.substr(0, active_lease.size()), active_lease)) {
            return true;
        }
        return false;
    }

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

    [[nodiscard]] static constexpr bool IsReservedDosDeviceName(std::u16string_view stem) noexcept {
        if (stem.size() == 3) {
            return vn_equals_icase_ascii(stem, u"CON") ||
                   vn_equals_icase_ascii(stem, u"PRN") ||
                   vn_equals_icase_ascii(stem, u"AUX") ||
                   vn_equals_icase_ascii(stem, u"NUL");
        }
        if (stem.size() == 4) {
            const std::u16string_view prefix = stem.substr(0, 3);
            if (vn_equals_icase_ascii(prefix, u"COM") || vn_equals_icase_ascii(prefix, u"LPT")) {
                const char16_t d = stem[3];
                return (d >= u'0' && d <= u'9') || d == u'\u00B9' || d == u'\u00B2' || d == u'\u00B3';
            }
        }
        if (stem.size() == 6) {
            return vn_equals_icase_ascii(stem, u"CONIN$");
        }
        if (stem.size() == 7) {
            return vn_equals_icase_ascii(stem, u"CONOUT$");
        }
        return false;
    }

    [[nodiscard]] static constexpr bool IsValidRelativeSegment(std::u16string_view seg) noexcept {
        if (seg.empty() || seg.back() == u' ' || seg.back() == u'.') {
            return false;
        }
        const size_t dot_pos = seg.find(u'.');
        std::u16string_view stem = (dot_pos == std::u16string_view::npos) ? seg : seg.substr(0, dot_pos);
        while (!stem.empty() && (stem.back() == u' ' || stem.back() == u'.')) {
            stem.remove_suffix(1);
        }
        return !IsReservedDosDeviceName(stem);
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
        for (char16_t ch : root_prefix) {
            if (ch == u'\0') return false;
        }
        if (root_prefix.find(u':') != std::u16string_view::npos) {
            const char16_t c0 = root_prefix[0];
            const bool valid_drive = ((c0 >= u'A' && c0 <= u'Z') || (c0 >= u'a' && c0 <= u'z')) &&
                                     root_prefix.size() >= 3 && root_prefix[1] == u':' &&
                                     (root_prefix[2] == u'\\' || root_prefix[2] == u'/') &&
                                     root_prefix.substr(2).find(u':') == std::u16string_view::npos;
            if (!valid_drive) {
                return false;
            }
        }
        for (char16_t ch : resolved_path) {
            if (ch == u'\0') return false;
        }
        if (!vnotch::starts_with_ignore_case(resolved_path, root_prefix)) {
            return false;
        }
        std::u16string_view rel = resolved_path.substr(root_prefix.size());
        const bool root_has_sep = (root_prefix.back() == u'\\' || root_prefix.back() == u'/');
        if (!root_has_sep) {
            if (rel.empty() || (rel.front() != u'\\' && rel.front() != u'/')) {
                return false;
            }
            rel.remove_prefix(1);
        }
        if (!rel.empty() && (rel.back() == u'\\' || rel.back() == u'/')) {
            rel.remove_suffix(1);
        }
        if (rel.empty() || rel.front() == u'\\' || rel.front() == u'/' ||
            rel.back() == u'\\' || rel.back() == u'/') {
            return false;
        }
        for (char16_t ch : rel) {
            if (ch < 32 || ch == u':' || ch == u'<' || ch == u'>' ||
                ch == u'"' || ch == u'|' || ch == u'?' || ch == u'*') {
                return false;
            }
        }
        size_t start = 0;
        while (start < rel.size()) {
            size_t end = start;
            while (end < rel.size() && rel[end] != u'\\' && rel[end] != u'/') {
                ++end;
            }
            if (!IsValidRelativeSegment(rel.substr(start, end - start))) {
                return false;
            }
            start = end + 1;
        }
        return true;
    }
};

} // namespace vnotch::services::clipboard

extern "C" {

VN_EXPORT int32_t __stdcall vn_clipboard_store_accumulate_category_counts(
    const VnClipboardEntryCategoryMeta* entries,
    int32_t entry_count,
    VnClipboardCategoryCountsOut* out_counts) noexcept {
    try {
        if (!out_counts || entry_count < 0 || (entry_count > 0 && !entries)) return VN_E_INVALID_ARG;
        vnotch::services::clipboard::ClipboardHistoryStore::AccumulateCategoryCounts(entries, entry_count, *out_counts);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_tray_compute_reconcile_diff(
    const int32_t* desired_to_existing_index,
    int32_t desired_count,
    uint8_t* out_existing_survivor_mask,
    int32_t existing_count,
    int32_t* out_changes) noexcept {
    try {
        if (!out_changes || desired_count < 0 || existing_count < 0 ||
            (desired_count > 0 && !desired_to_existing_index) ||
            (existing_count > 0 && !out_existing_survivor_mask)) {
            return VN_E_INVALID_ARG;
        }
        *out_changes = vnotch::services::clipboard::ClipboardHistoryStore::ComputeReconcileDiff(
            desired_to_existing_index, desired_count, out_existing_survivor_mask, existing_count);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_card_compute_age_bucket(
    int64_t now_utc_ticks,
    int64_t copied_utc_ticks,
    int32_t* out_bucket,
    int32_t* out_value) noexcept {
    try {
        if (!out_bucket || !out_value) return VN_E_INVALID_ARG;
        vnotch::services::clipboard::ClipboardHistoryStore::ComputeAgeBucket(
            now_utc_ticks, copied_utc_ticks, *out_bucket, *out_value);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
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
    try {
        if (!out_result || (!category && category_len > 0) || category_len < 0) return VN_E_INVALID_ARG;
        const std::u16string_view cat(category ? category : u"", static_cast<size_t>(category_len));
        *out_result = vnotch::services::clipboard::ClipboardHistoryStore::MatchesCategoryCore(
            is_personal != 0, is_archived != 0, is_pinned != 0, kind, include_archived != 0, cat);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_store_is_screenshot_file_name(
    const char16_t* name,
    int32_t name_len,
    uint8_t* out_is_screenshot) noexcept {
    try {
        if (!out_is_screenshot || (!name && name_len > 0) || name_len < 0) return VN_E_INVALID_ARG;
        const std::u16string_view sv(name ? name : u"", static_cast<size_t>(name_len));
        *out_is_screenshot = vnotch::services::clipboard::ClipboardHistoryStore::IsScreenshotFileName(sv) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_store_is_screenshot_source_app(
    const char16_t* source_app,
    int32_t source_app_len,
    uint8_t* out_is_screenshot_app) noexcept {
    try {
        if (!out_is_screenshot_app || (!source_app && source_app_len > 0) || source_app_len < 0) return VN_E_INVALID_ARG;
        const std::u16string_view sv(source_app ? source_app : u"", static_cast<size_t>(source_app_len));
        *out_is_screenshot_app = vnotch::services::clipboard::ClipboardHistoryStore::IsScreenshotSourceApp(sv) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_store_is_sharing_violation_hresult(
    int32_t hresult,
    uint8_t* out_is_sharing_violation) noexcept {
    try {
        if (!out_is_sharing_violation) return VN_E_INVALID_ARG;
        *out_is_sharing_violation = vnotch::services::clipboard::ClipboardHistoryStore::IsSharingViolationHResult(hresult) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
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
    try {
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
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_equals_icase(
    const char16_t* a,
    int32_t a_len,
    const char16_t* b,
    int32_t b_len,
    uint8_t* out_equals) noexcept {
    try {
        if (!out_equals ||
            (!a && a_len > 0) || a_len < 0 ||
            (!b && b_len > 0) || b_len < 0) {
            return VN_E_INVALID_ARG;
        }
        const std::u16string_view a_sv(a ? a : u"", static_cast<size_t>(a_len));
        const std::u16string_view b_sv(b ? b : u"", static_cast<size_t>(b_len));
        *out_equals = vnotch::services::clipboard::ClipboardHistoryStore::EqualsOrdinalIgnoreCase(a_sv, b_sv) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_plan_prune(
    const VnClipboardPruneCandidate* candidates,
    int32_t candidate_count,
    int64_t cutoff_utc_ticks,
    int32_t max_unpinned_entries,
    int64_t max_unpinned_bytes,
    int32_t* out_ordered_indices,
    int32_t max_indices,
    int32_t* out_expired_count,
    int32_t* out_evict_candidate_count,
    int64_t* out_total_active_bytes) noexcept {
    try {
        if (!out_expired_count || !out_evict_candidate_count || !out_total_active_bytes ||
            candidate_count < 0 || max_indices < 0 ||
            (candidate_count > 0 && !candidates) ||
            (max_indices > 0 && !out_ordered_indices)) {
            return VN_E_INVALID_ARG;
        }
        vnotch::services::clipboard::ClipboardHistoryStore::PlanPrune(
            candidates,
            candidate_count,
            cutoff_utc_ticks,
            max_unpinned_entries,
            max_unpinned_bytes,
            out_ordered_indices,
            max_indices,
            *out_expired_count,
            *out_evict_candidate_count,
            *out_total_active_bytes);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_evaluate_sensitive_expiry(
    uint8_t has_entry,
    uint8_t is_pinned,
    uint8_t is_archived,
    uint8_t is_personal,
    uint8_t has_personal_dir,
    uint8_t ticks_valid,
    int64_t marker_ticks,
    int64_t expiry_cutoff_ticks,
    int32_t* out_action) noexcept {
    try {
        if (!out_action) return VN_E_INVALID_ARG;
        *out_action = vnotch::services::clipboard::ClipboardHistoryStore::EvaluateSensitiveExpiry(
            has_entry != 0, is_pinned != 0, is_archived != 0, is_personal != 0,
            has_personal_dir != 0, ticks_valid != 0, marker_ticks, expiry_cutoff_ticks);
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_vault_stream_magic(
    const uint8_t* magic,
    int32_t magic_len,
    uint8_t* out_is_magic) noexcept {
    try {
        if (!out_is_magic || magic_len < 0 || (magic_len > 0 && !magic)) return VN_E_INVALID_ARG;
        *out_is_magic = vnotch::services::clipboard::ClipboardHistoryStore::IsVaultStreamMagic(magic, magic_len) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_format_prefixed_sha256_hex(
    const char16_t* prefix,
    int32_t prefix_len,
    const uint8_t* hash_bytes,
    int32_t hash_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept {
    try {
        if (!out_written || (!prefix && prefix_len > 0) || prefix_len < 0 ||
            (!hash_bytes && hash_len > 0) || hash_len < 0 ||
            (!out_buf && out_cap > 0) || out_cap < 0) {
            return VN_E_INVALID_ARG;
        }
        const std::u16string_view pref_sv(prefix ? prefix : u"", static_cast<size_t>(prefix_len));
        if (!vnotch::services::clipboard::ClipboardHistoryStore::FormatPrefixedSha256Hex(
                pref_sv, hash_bytes, hash_len, out_buf, out_cap, *out_written)) {
            return VN_ERR_BUFFER_TOO_SMALL;
        }
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_are_signatures_equal(
    const char16_t* sig_a,
    int32_t len_a,
    const char16_t* sig_b,
    int32_t len_b,
    uint8_t* out_equal) noexcept {
    try {
        if (!out_equal ||
            (!sig_a && len_a > 0) || len_a < 0 ||
            (!sig_b && len_b > 0) || len_b < 0) {
            return VN_E_INVALID_ARG;
        }
        const std::u16string_view a_sv(sig_a ? sig_a : u"", static_cast<size_t>(len_a));
        const std::u16string_view b_sv(sig_b ? sig_b : u"", static_cast<size_t>(len_b));
        *out_equal = vnotch::services::clipboard::ClipboardHistoryStore::AreSignaturesEqual(a_sv, b_sv) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_probable_duplicate_image_candidate(
    int32_t kind,
    uint8_t is_personal,
    int64_t capture_ticks,
    int64_t entry_ticks,
    int64_t best_ticks,
    uint8_t has_best,
    uint8_t* out_is_better) noexcept {
    try {
        if (!out_is_better) return VN_E_INVALID_ARG;
        *out_is_better = vnotch::services::clipboard::ClipboardHistoryStore::IsProbableDuplicateImageCandidate(
            kind, is_personal != 0, capture_ticks, entry_ticks, best_ticks, has_best != 0) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t __stdcall vn_clipboard_store_is_export_path_protected(
    const char16_t* directory,
    int32_t directory_len,
    const char16_t* active_lease,
    int32_t active_lease_len,
    uint16_t dir_sep,
    uint8_t* out_protected) noexcept {
    try {
        if (!out_protected ||
            (!directory && directory_len > 0) || directory_len < 0 ||
            (!active_lease && active_lease_len > 0) || active_lease_len < 0) {
            return VN_E_INVALID_ARG;
        }
        const std::u16string_view dir_sv(directory ? directory : u"", static_cast<size_t>(directory_len));
        const std::u16string_view lease_sv(active_lease ? active_lease : u"", static_cast<size_t>(active_lease_len));
        *out_protected = vnotch::services::clipboard::ClipboardHistoryStore::IsExportPathProtectedByLease(
            dir_sv, lease_sv, static_cast<char16_t>(dir_sep)) ? 1 : 0;
        return VN_OK;
    } catch (...) {
        return VN_ERR_FAIL;
    }
}

} // extern "C"

VN_EXPORT int32_t __stdcall vn_clipboard_store_filter_entries_batch(const VnClipboardSearchEntry* entries, int32_t count,
    const VnClipboardSearchSlice* groups, int32_t group_count, const VnClipboardSearchLabel* labels, int32_t label_count,
    const char16_t* pool, int32_t pool_length, const char16_t* query, int32_t query_length,
    const char16_t* category, int32_t category_length, uint8_t personal, uint8_t* matches, int32_t capacity) noexcept {
    using Store = vnotch::services::clipboard::ClipboardHistoryStore;
    if (count < 0 || group_count < 0 || label_count < 0 || pool_length < 0 || query_length < 0 || category_length < 0 || capacity < count ||
        (count && (!entries || !matches)) || (group_count && !groups) || (label_count && !labels) || (pool_length && !pool) ||
        (query_length && !query) || (category_length && !category)) return VN_ERR_INVALID_ARG;
    auto valid = [pool_length](VnClipboardSearchSlice slice) {
        return slice.offset >= 0 && slice.length >= 0 && slice.offset <= pool_length && slice.length <= pool_length - slice.offset;
    };
    auto view = [pool](VnClipboardSearchSlice slice) { return std::u16string_view(pool ? pool + slice.offset : u"", slice.length); };
    for (int32_t i = 0; i < group_count; ++i) if (!valid(groups[i])) return VN_ERR_INVALID_ARG;
    for (int32_t i = 0; i < label_count; ++i) if (!valid(labels[i].id) || !valid(labels[i].name)) return VN_ERR_INVALID_ARG;
    for (int32_t i = 0; i < count; ++i) {
        const auto& e = entries[i];
        if (!valid(e.title) || !valid(e.preview) || !valid(e.source) || (e.full_text.length != -1 && !valid(e.full_text)) ||
            e.group_start < 0 || e.group_count < 0 || e.group_start > group_count || e.group_count > group_count - e.group_start) return VN_ERR_INVALID_ARG;
    }
    int32_t starts[256]{}, lengths[256]{};
    const std::u16string_view query_view(query ? query : u"", query_length);
    const int32_t token_count = Store::TokenizeQuery(query_view, starts, lengths, 256);
    const std::u16string_view category_view(category ? category : u"", category_length);
    auto in_category = [&](const VnClipboardSearchEntry& e, std::u16string_view value, bool archived) {
        const auto result = Store::MatchesCategoryCore(e.personal != 0, e.archived != 0, e.pinned != 0, e.kind, archived, value);
        if (result >= 0) return result != 0;
        for (int32_t g = 0; g < e.group_count; ++g) if (Store::EqualsOrdinalIgnoreCase(view(groups[e.group_start + g]), value)) return true;
        return false;
    };
    for (int32_t i = 0; i < count; ++i) {
        const auto& e = entries[i];
        matches[i] = 0;
        if ((e.personal != 0) != (personal != 0) || !in_category(e, category_view, false)) continue;
        uint8_t result = 1;
        for (int32_t t = 0; t < token_count; ++t) {
            auto token = query_view.substr(starts[t], lengths[t]);
            if (token.front() == u'@') {
                if (!in_category(e, token.substr(1), true)) { result = 0; break; }
                continue;
            }
            bool match = Store::EntryMatchesMetadataToken(e.sensitive != 0, view(e.title), view(e.preview), view(e.source), token);
            for (int32_t g = 0; !match && g < e.group_count; ++g) {
                const auto group = view(groups[e.group_start + g]);
                match = Store::ContainsOrdinalIgnoreCase(group, token);
                for (int32_t l = 0; !match && l < label_count; ++l)
                    if (Store::EqualsOrdinalIgnoreCase(group, view(labels[l].id))) {
                        match = Store::ContainsOrdinalIgnoreCase(view(labels[l].name), token); break;
                    }
            }
            if (match) continue;
            if (e.sensitive) { result = 0; break; }
            if (e.full_text.length == -1) { result = 2; continue; }
            if (!Store::ContainsOrdinalIgnoreCase(view(e.full_text), token)) { result = 0; break; }
        }
        matches[i] = result;
    }
    return VN_OK;
}
