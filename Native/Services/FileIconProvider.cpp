#include "../vnotch_common.h"
#include <algorithm>
#include <string_view>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnFileIconExtensionFlags {
    uint8_t is_image;
    uint8_t is_video;
    uint8_t is_svg;
    uint8_t is_exe_or_lnk;
    uint8_t is_msc;
    uint8_t is_lnk;
};
#pragma pack(pop)

class FileIconProvider final {
public:
    static constexpr void ClassifyExtension(std::u16string_view ext, VnFileIconExtensionFlags& out) noexcept {
        out = {};
        if (ext.empty()) return;

        if (vnotch::vn_equals_icase_ascii(ext, u".svg")) {
            out.is_image = 1u;
            out.is_svg = 1u;
            return;
        }
        if (vnotch::vn_equals_icase_ascii(ext, u".jpg") ||
            vnotch::vn_equals_icase_ascii(ext, u".jpeg") ||
            vnotch::vn_equals_icase_ascii(ext, u".png") ||
            vnotch::vn_equals_icase_ascii(ext, u".bmp") ||
            vnotch::vn_equals_icase_ascii(ext, u".gif") ||
            vnotch::vn_equals_icase_ascii(ext, u".webp") ||
            vnotch::vn_equals_icase_ascii(ext, u".ico")) {
            out.is_image = 1u;
            return;
        }
        if (vnotch::vn_equals_icase_ascii(ext, u".mp4") ||
            vnotch::vn_equals_icase_ascii(ext, u".mkv") ||
            vnotch::vn_equals_icase_ascii(ext, u".avi") ||
            vnotch::vn_equals_icase_ascii(ext, u".mov") ||
            vnotch::vn_equals_icase_ascii(ext, u".wmv") ||
            vnotch::vn_equals_icase_ascii(ext, u".flv") ||
            vnotch::vn_equals_icase_ascii(ext, u".webm")) {
            out.is_video = 1u;
            return;
        }
        if (vnotch::vn_equals_icase_ascii(ext, u".exe")) {
            out.is_exe_or_lnk = 1u;
            return;
        }
        if (vnotch::vn_equals_icase_ascii(ext, u".lnk")) {
            out.is_exe_or_lnk = 1u;
            out.is_lnk = 1u;
            return;
        }
        if (vnotch::vn_equals_icase_ascii(ext, u".msc")) {
            out.is_msc = 1u;
            return;
        }
    }

    static constexpr bool TryParseMscIconXml(
        std::u16string_view xml,
        int32_t& out_index,
        int32_t& out_file_start,
        int32_t& out_file_len) noexcept {
        out_index = 0;
        out_file_start = 0;
        out_file_len = 0;

        constexpr std::u16string_view kTag = u"<Icon";
        constexpr std::u16string_view kIndexAttr = u"Index=\"";
        constexpr std::u16string_view kFileAttr = u"File=\"";

        size_t pos = 0;
        while (pos + kTag.size() < xml.size()) {
            size_t found = xml.find(kTag, pos);
            if (found == std::u16string_view::npos) return false;

            size_t cur = found + kTag.size();
            if (cur >= xml.size() || !vnotch::vn_is_whitespace(xml[cur])) {
                pos = cur;
                continue;
            }
            while (cur < xml.size() && vnotch::vn_is_whitespace(xml[cur])) ++cur;

            if (cur + kIndexAttr.size() > xml.size() ||
                xml.substr(cur, kIndexAttr.size()) != kIndexAttr) {
                pos = cur;
                continue;
            }
            cur += kIndexAttr.size();

            bool neg = false;
            if (cur < xml.size() && xml[cur] == u'-') {
                neg = true;
                ++cur;
            }
            if (cur >= xml.size() || xml[cur] < u'0' || xml[cur] > u'9') {
                pos = cur;
                continue;
            }
            int64_t idx_val = 0;
            while (cur < xml.size() && xml[cur] >= u'0' && xml[cur] <= u'9') {
                idx_val = idx_val * 10 + (xml[cur] - u'0');
                if (idx_val > 0x7FFFFFFFLL) break;
                ++cur;
            }
            if (cur >= xml.size() || xml[cur] != u'"') {
                pos = cur;
                continue;
            }
            ++cur;

            if (cur >= xml.size() || !vnotch::vn_is_whitespace(xml[cur])) {
                pos = cur;
                continue;
            }
            while (cur < xml.size() && vnotch::vn_is_whitespace(xml[cur])) ++cur;

            if (cur + kFileAttr.size() > xml.size() ||
                xml.substr(cur, kFileAttr.size()) != kFileAttr) {
                pos = cur;
                continue;
            }
            cur += kFileAttr.size();
            size_t file_start = cur;
            while (cur < xml.size() && xml[cur] != u'"') ++cur;
            if (cur >= xml.size() || cur == file_start) {
                pos = cur;
                continue;
            }

            out_index = static_cast<int32_t>(neg ? -idx_val : idx_val);
            out_file_start = static_cast<int32_t>(file_start);
            out_file_len = static_cast<int32_t>(cur - file_start);
            return true;
        }
        return false;
    }

    static constexpr int32_t ComputeEvictCount(int32_t current_count, int32_t max_cache_size) noexcept {
        if (current_count < max_cache_size) return 0;
        return std::max(1, max_cache_size / 4);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_file_icon_provider_classify_extension(
    const char16_t* ext,
    int32_t ext_len,
    vnotch::services::VnFileIconExtensionFlags* out_flags) noexcept {
    if (!out_flags || ext_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(ext ? ext : u"", static_cast<size_t>(ext_len));
    vnotch::services::FileIconProvider::ClassifyExtension(sv, *out_flags);
    return VN_OK;
}

VN_EXPORT int32_t vn_file_icon_provider_parse_msc_icon_xml(
    const char16_t* xml,
    int32_t xml_len,
    int32_t* out_index,
    int32_t* out_file_start,
    int32_t* out_file_len,
    uint8_t* out_matched) noexcept {
    if (!out_index || !out_file_start || !out_file_len || !out_matched || xml_len < 0) return VN_E_INVALID_ARG;
    std::u16string_view sv(xml ? xml : u"", static_cast<size_t>(xml_len));
    *out_matched = vnotch::services::FileIconProvider::TryParseMscIconXml(
        sv, *out_index, *out_file_start, *out_file_len) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_file_icon_provider_compute_evict_count(
    int32_t current_count,
    int32_t max_cache_size,
    int32_t* out_remove_count) noexcept {
    if (!out_remove_count) return VN_E_INVALID_ARG;
    *out_remove_count = vnotch::services::FileIconProvider::ComputeEvictCount(current_count, max_cache_size);
    return VN_OK;
}

} // extern "C"
