#include "vnotch_common.h"
#include <array>
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class BandicamRecordingProbe final {
public:
    static bool IsSupportedOutputPath(std::u16string_view path) noexcept {
        if (path.size() < 7) return false;
        char16_t drive = path[0];
        bool is_ascii_letter = (drive >= u'A' && drive <= u'Z') || (drive >= u'a' && drive <= u'z');
        if (!is_ascii_letter || path[1] != u':' || path[2] != u'\\') {
            return false;
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
