#include "vnotch_common.h"
#include <cstdint>
#include <string_view>

namespace vnotch::services::translation {

class NativeSelectionReader final {
public:
    NativeSelectionReader() = delete;

    [[nodiscard]] static constexpr bool IsEdit(std::u16string_view name) noexcept {
        return vn_equals_icase_ascii(name, u"Edit") ||
               vn_starts_with_icase_ascii(name, u"RichEdit") ||
               vn_starts_with_icase_ascii(name, u"WindowsForms10.EDIT.") ||
               vn_starts_with_icase_ascii(name, u"WindowsForms10.RichEdit") ||
               vn_contains_icase_ascii(name, u".EDIT.") ||
               vn_contains_icase_ascii(name, u"RichEdit") ||
               vn_equals_icase_ascii(name, u"TEdit") ||
               vn_equals_icase_ascii(name, u"TMemo") ||
               vn_equals_icase_ascii(name, u"TRichEdit") ||
               vn_equals_icase_ascii(name, u"TSynEdit") ||
               vn_equals_icase_ascii(name, u"ThunderRT6TextBox");
    }

    [[nodiscard]] static constexpr bool IsScintilla(std::u16string_view name) noexcept {
        return vn_equals_icase_ascii(name, u"Scintilla") ||
               vn_contains_icase_ascii(name, u"Scintilla");
    }

    [[nodiscard]] static constexpr bool IsComboBox(std::u16string_view name) noexcept {
        return vn_equals_icase_ascii(name, u"ComboBox") ||
               vn_equals_icase_ascii(name, u"ComboBoxEx32") ||
               vn_starts_with_icase_ascii(name, u"WindowsForms10.COMBOBOX.") ||
               vn_contains_icase_ascii(name, u".COMBOBOX.") ||
               vn_equals_icase_ascii(name, u"TComboBox");
    }

    [[nodiscard]] static constexpr bool IsListBox(std::u16string_view name) noexcept {
        return vn_equals_icase_ascii(name, u"ListBox") ||
               vn_starts_with_icase_ascii(name, u"WindowsForms10.LISTBOX.") ||
               vn_contains_icase_ascii(name, u".LISTBOX.") ||
               vn_equals_icase_ascii(name, u"TListBox");
    }

    [[nodiscard]] static constexpr bool ResolveSelectionRange(
        int32_t raw_start,
        int32_t raw_end,
        int64_t packed_selection,
        int32_t& out_start,
        int32_t& out_end) noexcept {
        out_start = raw_start;
        out_end = raw_end;
        if (out_end <= out_start || out_start < 0) {
            const uint32_t packed = static_cast<uint32_t>(packed_selection);
            if (packed == 0xFFFFFFFFU) return false;
            out_start = static_cast<int32_t>(packed & 0xFFFFU);
            out_end = static_cast<int32_t>(packed >> 16);
        }
        return out_end > out_start && out_start >= 0;
    }
};

} // namespace vnotch::services::translation

extern "C" {

VN_EXPORT int32_t vn_native_selection_is_edit(
    const char16_t* name,
    int32_t name_len,
    uint8_t* out_res) noexcept {
    if (!out_res || (!name && name_len > 0) || name_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(name ? name : u"", static_cast<size_t>(name_len));
    *out_res = vnotch::services::translation::NativeSelectionReader::IsEdit(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_native_selection_is_scintilla(
    const char16_t* name,
    int32_t name_len,
    uint8_t* out_res) noexcept {
    if (!out_res || (!name && name_len > 0) || name_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(name ? name : u"", static_cast<size_t>(name_len));
    *out_res = vnotch::services::translation::NativeSelectionReader::IsScintilla(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_native_selection_is_combobox(
    const char16_t* name,
    int32_t name_len,
    uint8_t* out_res) noexcept {
    if (!out_res || (!name && name_len > 0) || name_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(name ? name : u"", static_cast<size_t>(name_len));
    *out_res = vnotch::services::translation::NativeSelectionReader::IsComboBox(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_native_selection_is_listbox(
    const char16_t* name,
    int32_t name_len,
    uint8_t* out_res) noexcept {
    if (!out_res || (!name && name_len > 0) || name_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(name ? name : u"", static_cast<size_t>(name_len));
    *out_res = vnotch::services::translation::NativeSelectionReader::IsListBox(sv) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_native_selection_resolve_range(
    int32_t raw_start,
    int32_t raw_end,
    int64_t packed_selection,
    int32_t* out_start,
    int32_t* out_end,
    uint8_t* out_valid) noexcept {
    if (!out_start || !out_end || !out_valid) return VN_E_INVALID_ARG;
    int32_t s = 0, e = 0;
    const bool ok = vnotch::services::translation::NativeSelectionReader::ResolveSelectionRange(
        raw_start, raw_end, packed_selection, s, e);
    *out_start = s;
    *out_end = e;
    *out_valid = ok ? 1 : 0;
    return VN_OK;
}

} // extern "C"
