#include "../vnotch_common.h"
#include <algorithm>
#include <string_view>

namespace vnotch::services {

class LocalizationService final {
public:
    static std::u16string_view ResolveDefaultCultureCode(std::u16string_view lang) noexcept {
        if (vnotch::vn_equals_icase_ascii(lang, u"vi")) return u"vi-VN";
        if (vnotch::vn_equals_icase_ascii(lang, u"zh")) return u"zh-CN";
        if (vnotch::vn_equals_icase_ascii(lang, u"pt")) return u"pt-BR";
        if (vnotch::vn_equals_icase_ascii(lang, u"ru")) return u"ru-RU";
        if (vnotch::vn_equals_icase_ascii(lang, u"ar")) return u"ar-SA";
        if (vnotch::vn_equals_icase_ascii(lang, u"ko")) return u"ko-KR";
        if (vnotch::vn_equals_icase_ascii(lang, u"es")) return u"es-ES";
        if (vnotch::vn_equals_icase_ascii(lang, u"fr")) return u"fr-FR";
        if (vnotch::vn_equals_icase_ascii(lang, u"de")) return u"de-DE";
        if (vnotch::vn_equals_icase_ascii(lang, u"ja")) return u"ja-JP";
        if (vnotch::vn_equals_icase_ascii(lang, u"hi")) return u"hi-IN";
        if (vnotch::vn_equals_icase_ascii(lang, u"it")) return u"it-IT";
        if (vnotch::vn_equals_icase_ascii(lang, u"tr")) return u"tr-TR";
        if (vnotch::vn_equals_icase_ascii(lang, u"pl")) return u"pl-PL";
        if (vnotch::vn_equals_icase_ascii(lang, u"nl")) return u"nl-NL";
        if (vnotch::vn_equals_icase_ascii(lang, u"id")) return u"id-ID";
        return u"en-US";
    }

    static constexpr bool TryExtractEmbeddedResourceLocaleCode(
        std::u16string_view resource_name,
        int32_t& out_start,
        int32_t& out_len) noexcept {
        constexpr std::u16string_view kPrefix = u"VNotch.Locales.";
        constexpr std::u16string_view kSuffix = u".json";
        if (resource_name.size() <= kPrefix.size() + kSuffix.size()) {
            out_start = 0;
            out_len = 0;
            return false;
        }
        if (!vnotch::vn_starts_with_icase_ascii(resource_name, kPrefix) ||
            !vnotch::vn_ends_with_icase_ascii(resource_name, kSuffix)) {
            out_start = 0;
            out_len = 0;
            return false;
        }
        out_start = static_cast<int32_t>(kPrefix.size());
        out_len = static_cast<int32_t>(resource_name.size() - kPrefix.size() - kSuffix.size());
        return out_len > 0;
    }

    static constexpr bool ShouldStoreLocaleEntry(
        std::u16string_view prop_name,
        std::u16string_view text,
        bool key_exists) noexcept {
        if (prop_name.empty() || prop_name.front() == u'_') return false;
        if (text == prop_name) return false;
        return !vnotch::vn_trim(text).empty() || !key_exists;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_localization_resolve_default_culture(
    const char16_t* lang,
    int32_t lang_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept {
    if (!out_buf || out_cap < 5 || !out_len || lang_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(lang ? lang : u"", static_cast<size_t>(lang_len));
    std::u16string_view resolved = vnotch::services::LocalizationService::ResolveDefaultCultureCode(sv);
    const int32_t copy_len = std::min(static_cast<int32_t>(resolved.size()), out_cap);
    for (int32_t i = 0; i < copy_len; ++i) {
        out_buf[i] = resolved[static_cast<size_t>(i)];
    }
    *out_len = copy_len;
    return VN_OK;
}

VN_EXPORT int32_t vn_localization_extract_resource_code(
    const char16_t* resource_name,
    int32_t resource_name_len,
    int32_t* out_start,
    int32_t* out_len,
    uint8_t* out_matched) noexcept {
    if (!out_start || !out_len || !out_matched || resource_name_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(resource_name ? resource_name : u"", static_cast<size_t>(resource_name_len));
    *out_matched = vnotch::services::LocalizationService::TryExtractEmbeddedResourceLocaleCode(
        sv, *out_start, *out_len) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_localization_should_store_entry(
    const char16_t* prop_name,
    int32_t prop_name_len,
    const char16_t* text,
    int32_t text_len,
    uint8_t key_exists,
    uint8_t* out_should_store) noexcept {
    if (!out_should_store || prop_name_len < 0 || text_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view prop_sv(prop_name ? prop_name : u"", static_cast<size_t>(prop_name_len));
    std::u16string_view text_sv(text ? text : u"", static_cast<size_t>(text_len));
    *out_should_store = vnotch::services::LocalizationService::ShouldStoreLocaleEntry(
        prop_sv, text_sv, key_exists != 0) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
