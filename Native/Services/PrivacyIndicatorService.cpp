#include "../vnotch_common.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cwchar>
#include <string_view>

#pragma comment(lib, "advapi32.lib")

#ifndef VN_PRIVACY_INDICATOR_BATCH_DEFINED
#define VN_PRIVACY_INDICATOR_BATCH_DEFINED
#pragma pack(push, 1)
struct VnConsentStoreEntry {
    int64_t last_start_file_time;
    int32_t raw_len;
    int32_t display_start;
    int32_t display_len;
    char16_t raw_name[260];
};

struct VnActiveAppSlice {
    int32_t name_offset;
    int32_t name_len;
    int64_t last_start_file_time;
};
#pragma pack(pop)
#endif

namespace vnotch::services {

struct ScopedRegKey {
    HKEY key = nullptr;
    ~ScopedRegKey() noexcept {
        if (key) ::RegCloseKey(key);
    }
};

class PrivacyIndicatorService final {
public:
    static constexpr int64_t kFileTimeOffsetTicks = 504911232000000000LL;
    static constexpr int64_t kMaxFileTime = 2650467743999999999LL;

    static constexpr bool HasMinimumActiveDuration(
        int64_t start_file_time,
        int64_t utc_now_ticks,
        int64_t minimum_ticks) noexcept {
        if (start_file_time <= 0 || start_file_time > kMaxFileTime || minimum_ticks < 0) {
            return false;
        }
        const int64_t start_ticks = start_file_time + kFileTimeOffsetTicks;
        return start_ticks <= utc_now_ticks && (utc_now_ticks - start_ticks) >= minimum_ticks;
    }

    static constexpr bool IsIgnoredMicrophoneProcessName(std::u16string_view process_name) noexcept {
        if (vnotch::vn_equals_icase_ascii(process_name, u"audiodg") ||
            vnotch::vn_equals_icase_ascii(process_name, u"svchost") ||
            vnotch::vn_equals_icase_ascii(process_name, u"system") ||
            vnotch::vn_equals_icase_ascii(process_name, u"registry") ||
            vnotch::vn_equals_icase_ascii(process_name, u"elgato.wavelink")) {
            return true;
        }
        if (vnotch::vn_ends_with_icase_ascii(process_name, u"service") ||
            vnotch::vn_ends_with_icase_ascii(process_name, u"services") ||
            vnotch::vn_ends_with_icase_ascii(process_name, u"svc") ||
            vnotch::vn_ends_with_icase_ascii(process_name, u"daemon")) {
            return true;
        }
        return false;
    }

    static constexpr void NormalizeAppNameSlice(
        std::u16string_view raw,
        int32_t& out_start,
        int32_t& out_len) noexcept {
        out_start = 0;
        out_len = static_cast<int32_t>(raw.size());
        if (vnotch::vn_trim(raw).empty()) return;

        const size_t last_hash = raw.rfind(u'#');
        if (last_hash != std::u16string_view::npos) {
            std::u16string_view last_part = raw.substr(last_hash + 1);
            if (!vnotch::vn_trim(last_part).empty()) {
                out_start = static_cast<int32_t>(last_hash + 1);
                out_len = static_cast<int32_t>(last_part.size());
                return;
            }
        }

        const size_t underscore = raw.find(u'_');
        if (underscore != std::u16string_view::npos && underscore > 0) {
            out_start = 0;
            out_len = static_cast<int32_t>(underscore);
        }
    }

    static bool EvaluateMicrophoneActivityGate(
        bool has_candidate,
        bool has_active_session,
        float peak_level,
        float signal_threshold,
        int64_t hold_duration_ticks,
        int64_t utc_now_ticks,
        int64_t& inout_last_signal_utc_ticks) noexcept {
        if (!has_candidate || !has_active_session) {
            inout_last_signal_utc_ticks = 0;
            return false;
        }

        if (std::isfinite(peak_level) && peak_level >= signal_threshold) {
            inout_last_signal_utc_ticks = utc_now_ticks;
        }

        if (inout_last_signal_utc_ticks == 0 || utc_now_ticks < inout_last_signal_utc_ticks) {
            return false;
        }

        return (utc_now_ticks - inout_last_signal_utc_ticks) <= hold_duration_ticks;
    }

    static bool TryDetectInUse(HKEY key, int64_t& out_last_start) noexcept {
        out_last_start = 0;
        uint64_t start_val = 0;
        DWORD start_size = sizeof(start_val);
        if (::RegGetValueW(key, nullptr, L"LastUsedTimeStart", RRF_RT_REG_QWORD, nullptr, &start_val, &start_size) != ERROR_SUCCESS) {
            return false;
        }
        uint64_t stop_val = 0;
        DWORD stop_size = sizeof(stop_val);
        if (::RegGetValueW(key, nullptr, L"LastUsedTimeStop", RRF_RT_REG_QWORD, nullptr, &stop_val, &stop_size) != ERROR_SUCCESS) {
            return false;
        }
        const int64_t signed_start = static_cast<int64_t>(start_val);
        const int64_t signed_stop = static_cast<int64_t>(stop_val);
        out_last_start = signed_start;
        return signed_start > 0 && signed_stop == 0;
    }

    static void AppendConsentEntry(
        const wchar_t* name,
        DWORD name_len,
        int64_t last_start,
        VnConsentStoreEntry* out_entries,
        int32_t entries_cap,
        int32_t& inout_count) noexcept {
        const int32_t clamped_len = static_cast<int32_t>(std::min<DWORD>(name_len, 259u));
        if (inout_count < entries_cap && out_entries != nullptr) {
            VnConsentStoreEntry& entry = out_entries[inout_count];
            entry.last_start_file_time = last_start;
            entry.raw_len = clamped_len;
            for (int32_t i = 0; i < clamped_len; ++i) {
                entry.raw_name[i] = static_cast<char16_t>(name[i]);
            }
            entry.raw_name[clamped_len] = u'\0';
            const std::u16string_view sv(entry.raw_name, static_cast<size_t>(clamped_len));
            NormalizeAppNameSlice(sv, entry.display_start, entry.display_len);
        }
        ++inout_count;
    }

    static void ScanConsentStoreHive(
        HKEY hive,
        const wchar_t* capability_subkey_path,
        VnConsentStoreEntry* out_entries,
        int32_t entries_cap,
        int32_t& inout_count) noexcept {
        ScopedRegKey cap_root;
        if (::RegOpenKeyExW(hive, capability_subkey_path, 0, KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE, &cap_root.key) != ERROR_SUCCESS || !cap_root.key) {
            return;
        }

        wchar_t subkey_name[260];
        for (DWORD idx = 0;; ++idx) {
            DWORD subkey_len = 259;
            const LSTATUS enum_status = ::RegEnumKeyExW(
                cap_root.key, idx, subkey_name, &subkey_len, nullptr, nullptr, nullptr, nullptr);
            if (enum_status == ERROR_NO_MORE_ITEMS) break;
            if (enum_status != ERROR_SUCCESS) continue;
            subkey_name[subkey_len] = L'\0';

            ScopedRegKey subkey;
            if (::RegOpenKeyExW(cap_root.key, subkey_name, 0, KEY_ENUMERATE_SUB_KEYS | KEY_QUERY_VALUE, &subkey.key) != ERROR_SUCCESS || !subkey.key) {
                continue;
            }

            int64_t last_start = 0;
            if (TryDetectInUse(subkey.key, last_start)) {
                AppendConsentEntry(subkey_name, subkey_len, last_start, out_entries, entries_cap, inout_count);
                continue;
            }

            const std::u16string_view subkey_sv(reinterpret_cast<const char16_t*>(subkey_name), static_cast<size_t>(subkey_len));
            if (vnotch::vn_equals_icase_ascii(subkey_sv, u"NonPackaged")) {
                wchar_t np_name[260];
                for (DWORD np_idx = 0;; ++np_idx) {
                    DWORD np_len = 259;
                    const LSTATUS np_status = ::RegEnumKeyExW(
                        subkey.key, np_idx, np_name, &np_len, nullptr, nullptr, nullptr, nullptr);
                    if (np_status == ERROR_NO_MORE_ITEMS) break;
                    if (np_status != ERROR_SUCCESS) continue;
                    np_name[np_len] = L'\0';

                    ScopedRegKey np_key;
                    if (::RegOpenKeyExW(subkey.key, np_name, 0, KEY_QUERY_VALUE, &np_key.key) == ERROR_SUCCESS && np_key.key) {
                        int64_t np_start = 0;
                        if (TryDetectInUse(np_key.key, np_start)) {
                            AppendConsentEntry(np_name, np_len, np_start, out_entries, entries_cap, inout_count);
                        }
                    }
                }
            }
        }
    }

    static int32_t ScanConsentStore(
        std::u16string_view capability,
        VnConsentStoreEntry* out_entries,
        int32_t entries_cap,
        int32_t& out_count) noexcept {
        out_count = 0;
        const std::u16string_view trimmed = vnotch::vn_trim(capability);
        if (trimmed.empty() || trimmed.size() > 128) {
            return VN_OK;
        }

        constexpr std::wstring_view kPrefix = L"Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\";
        wchar_t path[256];
        size_t pos = 0;
        for (wchar_t ch : kPrefix) {
            path[pos++] = ch;
        }
        for (char16_t ch : trimmed) {
            if (ch == u'\\' || ch == u'/') return VN_E_INVALID_ARG;
            path[pos++] = static_cast<wchar_t>(ch);
        }
        path[pos] = L'\0';

        int32_t total = 0;
        ScanConsentStoreHive(HKEY_CURRENT_USER, path, out_entries, entries_cap, total);
        ScanConsentStoreHive(HKEY_LOCAL_MACHINE, path, out_entries, entries_cap, total);
        out_count = total;
        return total > entries_cap ? VN_ERR_BUFFER_TOO_SMALL : VN_OK;
    }

    static bool EqualsOrdinalIgnoreCase(
        const char16_t* a,
        int32_t a_len,
        const char16_t* b,
        int32_t b_len) noexcept {
        if (a_len != b_len) return false;
        if (a_len <= 0) return true;
        return ::CompareStringOrdinal(
            reinterpret_cast<LPCWCH>(a),
            a_len,
            reinterpret_cast<LPCWCH>(b),
            b_len,
            TRUE) == CSTR_EQUAL;
    }

    static bool LessOrdinalIgnoreCase(
        const char16_t* a,
        int32_t a_len,
        const char16_t* b,
        int32_t b_len) noexcept {
        return ::CompareStringOrdinal(
            reinterpret_cast<LPCWCH>(a),
            a_len,
            reinterpret_cast<LPCWCH>(b),
            b_len,
            TRUE) == CSTR_LESS_THAN;
    }

    static int32_t MergeActiveApps(
        const VnActiveAppSlice* items,
        int32_t item_count,
        const char16_t* char_pool,
        int32_t char_pool_len,
        uint8_t sort_by_name,
        int32_t* out_indices,
        int32_t out_cap,
        int32_t& out_count) noexcept {
        out_count = 0;
        if (item_count <= 0) return VN_OK;

        int32_t unique_count = 0;
        for (int32_t i = 0; i < item_count; ++i) {
            const VnActiveAppSlice& cur = items[i];
            if (cur.name_offset < 0 || cur.name_len < 0 || cur.name_offset + cur.name_len > char_pool_len) {
                return VN_E_INVALID_ARG;
            }
            const char16_t* cur_ptr = char_pool + cur.name_offset;

            bool matched = false;
            const int32_t search_limit = std::min(unique_count, out_cap);
            for (int32_t u = 0; u < search_limit; ++u) {
                const int32_t prev_idx = out_indices[u];
                const VnActiveAppSlice& prev = items[prev_idx];
                const char16_t* prev_ptr = char_pool + prev.name_offset;
                if (EqualsOrdinalIgnoreCase(cur_ptr, cur.name_len, prev_ptr, prev.name_len)) {
                    if (sort_by_name == 0 && cur.last_start_file_time > prev.last_start_file_time) {
                        out_indices[u] = i;
                    }
                    matched = true;
                    break;
                }
            }

            if (!matched) {
                if (unique_count < out_cap && out_indices != nullptr) {
                    out_indices[unique_count] = i;
                }
                ++unique_count;
            }
        }

        out_count = unique_count;
        if (unique_count > out_cap) {
            return VN_ERR_BUFFER_TOO_SMALL;
        }

        if (sort_by_name != 0 && unique_count > 1 && out_indices != nullptr) {
            std::sort(out_indices, out_indices + unique_count, [&](int32_t lhs_idx, int32_t rhs_idx) noexcept {
                const VnActiveAppSlice& lhs = items[lhs_idx];
                const VnActiveAppSlice& rhs = items[rhs_idx];
                return LessOrdinalIgnoreCase(
                    char_pool + lhs.name_offset, lhs.name_len,
                    char_pool + rhs.name_offset, rhs.name_len);
            });
        }

        return VN_OK;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_privacy_indicator_has_min_active_duration(
    int64_t start_file_time,
    int64_t utc_now_ticks,
    int64_t minimum_ticks,
    uint8_t* out_active) noexcept {
    if (!out_active) return VN_E_INVALID_ARG;
    *out_active = vnotch::services::PrivacyIndicatorService::HasMinimumActiveDuration(
        start_file_time, utc_now_ticks, minimum_ticks) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_privacy_indicator_is_ignored_mic_process(
    const char16_t* process_name,
    int32_t process_name_len,
    uint8_t* out_ignored) noexcept {
    if (!out_ignored || process_name_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(process_name ? process_name : u"", static_cast<size_t>(process_name_len));
    *out_ignored = vnotch::services::PrivacyIndicatorService::IsIgnoredMicrophoneProcessName(sv) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_privacy_indicator_normalize_app_name_slice(
    const char16_t* raw,
    int32_t raw_len,
    int32_t* out_start,
    int32_t* out_len) noexcept {
    if (!out_start || !out_len || raw_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(raw ? raw : u"", static_cast<size_t>(raw_len));
    vnotch::services::PrivacyIndicatorService::NormalizeAppNameSlice(sv, *out_start, *out_len);
    return VN_OK;
}

VN_EXPORT int32_t vn_privacy_indicator_evaluate_mic_gate(
    uint8_t has_candidate,
    uint8_t has_active_session,
    float peak_level,
    float signal_threshold,
    int64_t hold_duration_ticks,
    int64_t utc_now_ticks,
    int64_t* inout_last_signal_utc_ticks,
    uint8_t* out_active) noexcept {
    if (!inout_last_signal_utc_ticks || !out_active) return VN_E_INVALID_ARG;
    *out_active = vnotch::services::PrivacyIndicatorService::EvaluateMicrophoneActivityGate(
        has_candidate != 0,
        has_active_session != 0,
        peak_level,
        signal_threshold,
        hold_duration_ticks,
        utc_now_ticks,
        *inout_last_signal_utc_ticks) ? 1u : 0u;
    return VN_OK;
}

extern "C" int32_t __stdcall vn_privacy_indicator_scan_consent_store(
    const char16_t* capability,
    int32_t capability_len,
    VnConsentStoreEntry* out_entries,
    int32_t entries_cap,
    int32_t* out_count) noexcept {
    if (!out_count || capability_len < 0 || (!capability && capability_len > 0) ||
        entries_cap < 0 || (!out_entries && entries_cap > 0)) {
        return VN_E_INVALID_ARG;
    }
    const std::u16string_view sv(capability ? capability : u"", static_cast<size_t>(capability_len));
    return vnotch::services::PrivacyIndicatorService::ScanConsentStore(sv, out_entries, entries_cap, *out_count);
}

VN_EXPORT int32_t __stdcall vn_privacy_indicator_merge_active_apps(
    const VnActiveAppSlice* items,
    int32_t item_count,
    const char16_t* char_pool,
    int32_t char_pool_len,
    uint8_t sort_by_name,
    int32_t* out_indices,
    int32_t out_cap,
    int32_t* out_count) noexcept {
    if (!out_count || item_count < 0 || (!items && item_count > 0) ||
        char_pool_len < 0 || (!char_pool && char_pool_len > 0) ||
        out_cap < 0 || (!out_indices && out_cap > 0)) {
        return VN_E_INVALID_ARG;
    }
    return vnotch::services::PrivacyIndicatorService::MergeActiveApps(
        items, item_count, char_pool, char_pool_len, sort_by_name, out_indices, out_cap, *out_count);
}

} // extern "C"
