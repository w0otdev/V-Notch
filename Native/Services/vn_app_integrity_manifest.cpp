#include "../vnotch_native.h"
#include <cwctype>

namespace {

inline bool is_ws(wchar_t c) noexcept {
    return (c >= L'\t' && c <= L'\r') ||
           c == L' ' ||
           c == 0x0085 ||
           c == 0x00A0 ||
           c == 0x1680 ||
           (c >= 0x2000 && c <= 0x200A) ||
           c == 0x2028 ||
           c == 0x2029 ||
           c == 0x202F ||
           c == 0x205F ||
           c == 0x3000 ||
           std::iswspace(static_cast<wint_t>(c)) != 0;
}

inline bool is_ctrl(wchar_t c) noexcept {
    return c <= 0x001F ||
           (c >= 0x007F && c <= 0x009F) ||
           std::iswcntrl(static_cast<wint_t>(c)) != 0;
}

constexpr bool is_hex_digit(wchar_t c) noexcept {
    return (c >= L'0' && c <= L'9') ||
           (c >= L'a' && c <= L'f') ||
           (c >= L'A' && c <= L'F');
}

constexpr bool is_hex64_sv(std::wstring_view sv) noexcept {
    if (sv.size() != 64) {
        return false;
    }
    for (wchar_t c : sv) {
        if (!is_hex_digit(c)) {
            return false;
        }
    }
    return true;
}

inline std::wstring_view trim_ws(std::wstring_view sv) noexcept {
    size_t start = 0;
    size_t end = sv.size();
    while (start < end && is_ws(sv[start])) {
        ++start;
    }
    while (end > start && is_ws(sv[end - 1])) {
        --end;
    }
    return sv.substr(start, end - start);
}

constexpr std::wstring_view trim_char(std::wstring_view sv, wchar_t ch) noexcept {
    size_t start = 0;
    size_t end = sv.size();
    while (start < end && sv[start] == ch) {
        ++start;
    }
    while (end > start && sv[end - 1] == ch) {
        --end;
    }
    return sv.substr(start, end - start);
}

constexpr bool is_quote_or_paren(wchar_t c) noexcept {
    return c == L'"' || c == L'\'' || c == L'(' || c == L')';
}

constexpr std::wstring_view trim_quote_parens(std::wstring_view sv) noexcept {
    size_t start = 0;
    size_t end = sv.size();
    while (start < end && is_quote_or_paren(sv[start])) {
        ++start;
    }
    while (end > start && is_quote_or_paren(sv[end - 1])) {
        --end;
    }
    return sv.substr(start, end - start);
}

constexpr std::wstring_view trim_start_char(std::wstring_view sv, wchar_t ch) noexcept {
    size_t start = 0;
    while (start < sv.size() && sv[start] == ch) {
        ++start;
    }
    return sv.substr(start);
}

inline std::wstring_view normalize_manifest_filename_sv(std::wstring_view raw_token) noexcept {
    std::wstring_view token = trim_ws(raw_token);
    token = trim_char(token, L':');
    token = trim_quote_parens(token);
    token = trim_start_char(token, L'*');
    token = trim_ws(token);

    size_t last_slash = token.find_last_of(L"/\\");
    if (last_slash != std::wstring_view::npos) {
        token = token.substr(last_slash + 1);
    }
    return trim_ws(token);
}

inline bool equals_ignore_case(std::wstring_view a, std::wstring_view b) noexcept {
    if (a.size() != b.size()) {
        return false;
    }
    if (a.empty()) {
        return true;
    }
    return CompareStringOrdinal(
               a.data(), static_cast<int>(a.size()),
               b.data(), static_cast<int>(b.size()),
               TRUE) == CSTR_EQUAL;
}

constexpr bool is_line_break(wchar_t c) noexcept {
    return c == L'\r' || c == L'\n' || c == 0x000B || c == 0x000C ||
           c == 0x0085 || c == 0x2028 || c == 0x2029;
}

} // namespace

VN_EXPORT int32_t __stdcall vn_app_integrity_is_valid_release_tag(
    const wchar_t* tag_ptr,
    int32_t tag_len,
    uint8_t* out_valid) noexcept
{
    if (!out_valid || tag_len < 0 || (tag_len > 0 && !tag_ptr)) {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = 0;
    if (!tag_ptr || tag_len == 0) {
        return VN_OK;
    }

    std::wstring_view tag(tag_ptr, static_cast<size_t>(tag_len));
    if (trim_ws(tag).empty()) {
        return VN_OK;
    }
    if (tag.find(L'/') != std::wstring_view::npos ||
        tag.find(L'\\') != std::wstring_view::npos ||
        tag.find(L"..") != std::wstring_view::npos) {
        return VN_OK;
    }
    for (wchar_t c : tag) {
        if (is_ctrl(c) || is_ws(c) || c == L'?' || c == L'#' || c == L'%') {
            return VN_OK;
        }
    }

    *out_valid = 1;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_app_integrity_matches_checksum_manifest(
    const wchar_t* manifest_ptr,
    int32_t manifest_len,
    const wchar_t* actual_hash_ptr,
    int32_t actual_hash_len,
    const wchar_t* target_file_ptr,
    int32_t target_file_len,
    uint8_t* out_matches) noexcept
{
    if (!out_matches || manifest_len < 0 || actual_hash_len < 0 || target_file_len < 0 ||
        (manifest_len > 0 && !manifest_ptr) ||
        (actual_hash_len > 0 && !actual_hash_ptr) ||
        (target_file_len > 0 && !target_file_ptr)) {
        return VN_ERR_INVALID_ARG;
    }
    *out_matches = 0;
    if (!manifest_ptr || manifest_len == 0 || !actual_hash_ptr || actual_hash_len == 0) {
        return VN_OK;
    }

    std::wstring_view manifest(manifest_ptr, static_cast<size_t>(manifest_len));
    if (trim_ws(manifest).empty()) {
        return VN_OK;
    }

    std::wstring_view expected_hash = trim_ws(std::wstring_view(actual_hash_ptr, static_cast<size_t>(actual_hash_len)));
    if (!is_hex64_sv(expected_hash)) {
        return VN_OK;
    }

    std::wstring_view raw_target = (target_file_ptr && target_file_len > 0)
        ? std::wstring_view(target_file_ptr, static_cast<size_t>(target_file_len))
        : std::wstring_view{};
    std::wstring_view normalized_target = normalize_manifest_filename_sv(raw_target);
    if (normalized_target.empty()) {
        normalized_target = L"V-Notch.exe";
    }

    size_t pos = 0;
    while (pos < manifest.size()) {
        size_t line_start = pos;
        while (pos < manifest.size() && !is_line_break(manifest[pos])) {
            ++pos;
        }
        std::wstring_view raw_line = manifest.substr(line_start, pos - line_start);
        if (pos < manifest.size()) {
            if (manifest[pos] == L'\r' && (pos + 1) < manifest.size() && manifest[pos + 1] == L'\n') {
                pos += 2;
            } else {
                ++pos;
            }
        }

        std::wstring_view line = trim_ws(raw_line);
        if (line.empty() || line[0] == L'#' || line[0] == L';') {
            continue;
        }

        size_t comment_idx = line.find(L'#');
        if (comment_idx != std::wstring_view::npos) {
            line = trim_ws(line.substr(0, comment_idx));
            if (line.empty()) {
                continue;
            }
        }

        bool found_matching_hash = false;
        int32_t file_token_count = 0;
        bool all_file_tokens_match = true;

        size_t i = 0;
        while (i < line.size()) {
            while (i < line.size() && (is_ws(line[i]) || line[i] == L':')) {
                ++i;
            }
            if (i >= line.size()) {
                break;
            }

            size_t start = i;
            while (i < line.size() && !is_ws(line[i]) && line[i] != L':') {
                ++i;
            }

            std::wstring_view token = line.substr(start, i - start);
            if (is_hex64_sv(token) && equals_ignore_case(token, expected_hash)) {
                found_matching_hash = true;
            } else {
                ++file_token_count;
                std::wstring_view normalized_file = normalize_manifest_filename_sv(token);
                if (normalized_file.empty() || !equals_ignore_case(normalized_file, normalized_target)) {
                    all_file_tokens_match = false;
                }
            }
        }

        if (found_matching_hash && (file_token_count == 0 || (file_token_count == 1 && all_file_tokens_match))) {
            *out_matches = 1;
            return VN_OK;
        }
    }

    return VN_OK;
}
