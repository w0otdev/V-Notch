#include "vnotch_common.h"
#include <array>
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class BandicamRecordingProbe final {
public:
    static bool IsSupportedOutputPath(std::u16string_view path) noexcept {
        if (path.size() < 7 || path.size() >= 32768) return false;
        char16_t drive = path[0];
        bool is_ascii_letter = (drive >= u'A' && drive <= u'Z') || (drive >= u'a' && drive <= u'z');
        if (!is_ascii_letter || path[1] != u':' || path[2] != u'\\') {
            return false;
        }

        size_t segment_start = 3;
        for (size_t i = 3; i <= path.size(); ++i) {
            const bool at_end = (i == path.size());
            const char16_t ch = at_end ? u'\\' : path[i];
            if (!at_end) {
                if (ch == u'\0' || ch == u'/' || ch == u':' || ch == u'<' || ch == u'>' ||
                    ch == u'"' || ch == u'|' || ch == u'?' || ch == u'*' || ch < 0x20) {
                    return false;
                }
            }
            if (ch == u'\\') {
                const auto segment = path.substr(segment_start, i - segment_start);
                if (segment.empty() || segment == u"." || segment == u".." ||
                    segment.back() == u' ' || segment.back() == u'.' || IsReservedDeviceName(segment)) {
                    return false;
                }
                segment_start = i + 1;
            }
        }

        std::u16string_view ext = path.substr(path.size() - 4);
        if (ext[0] != u'.') return false;

        char16_t c1 = ToLowerAscii(ext[1]);
        char16_t c2 = ToLowerAscii(ext[2]);
        char16_t c3 = ToLowerAscii(ext[3]);

        return (c1 == u'm' && c2 == u'p' && c3 == u'4') ||
               (c1 == u'a' && c2 == u'v' && c3 == u'i') ||
               (c1 == u'w' && c2 == u'a' && c3 == u'v') ||
               (c1 == u'm' && c2 == u'p' && c3 == u'3');
    }

private:
    static constexpr char16_t ToLowerAscii(char16_t c) noexcept {
        return (c >= u'A' && c <= u'Z') ? static_cast<char16_t>(c + 32) : c;
    }

    static bool EqualsIgnoreCaseAscii(std::u16string_view a, std::u16string_view b) noexcept {
        if (a.size() != b.size()) return false;
        for (size_t i = 0; i < a.size(); ++i) {
            if (ToLowerAscii(a[i]) != b[i]) return false;
        }
        return true;
    }

    static bool IsReservedDeviceName(std::u16string_view segment) noexcept {
        const size_t dot = segment.find(u'.');
        auto stem = dot == segment.npos ? segment : segment.substr(0, dot);
        while (!stem.empty() && stem.back() == u' ') stem.remove_suffix(1);
        if (stem.size() == 3) {
            return EqualsIgnoreCaseAscii(stem, u"con") || EqualsIgnoreCaseAscii(stem, u"prn") ||
                   EqualsIgnoreCaseAscii(stem, u"aux") || EqualsIgnoreCaseAscii(stem, u"nul");
        }
        if (stem.size() == 4) {
            const char16_t last = stem[3];
            if (last < u'1' || last > u'9') return false;
            return EqualsIgnoreCaseAscii(stem.substr(0, 3), u"com") ||
                   EqualsIgnoreCaseAscii(stem.substr(0, 3), u"lpt");
        }
        if (stem.size() == 6) return EqualsIgnoreCaseAscii(stem, u"conin$");
        if (stem.size() == 7) return EqualsIgnoreCaseAscii(stem, u"conout$");
        return false;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_bandicam_is_supported_output_path(
    const char16_t* path_ptr, int32_t path_len, uint8_t* out_supported) noexcept
{
    if (!out_supported) return VN_ERR_INVALID_ARG;
    if (!path_ptr || path_len <= 0) {
        *out_supported = 0;
        return VN_OK;
    }
    *out_supported = vnotch::services::BandicamRecordingProbe::IsSupportedOutputPath(
        std::u16string_view(path_ptr, static_cast<size_t>(path_len))) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
