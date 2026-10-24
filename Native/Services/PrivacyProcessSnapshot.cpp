#include "vnotch_common.h"
#include <windows.h>
#include <cstdint>
#include <string_view>

namespace vnotch::services {

class PrivacyProcessSnapshot final {
public:
    static bool MatchesExecutableName(const char16_t* entry_exe_file, std::u16string_view executable_name) noexcept {
        if (!entry_exe_file) return false;
        size_t len = 0;
        while (len < 260 && entry_exe_file[len] != u'\0') {
            len++;
        }
        std::u16string_view entry_sv(entry_exe_file, len);
        return vnotch::equals_ignore_case(entry_sv, executable_name);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_privproc_matches_exe_name(
    const char16_t* entry_exe_file,
    const char16_t* exe_name, int32_t exe_name_len,
    uint8_t* out_matches) noexcept
{
    if (!entry_exe_file || !out_matches) return VN_ERR_INVALID_ARG;
    *out_matches = vnotch::services::PrivacyProcessSnapshot::MatchesExecutableName(
        entry_exe_file, vnotch::make_u16_view(exe_name, exe_name_len)) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
