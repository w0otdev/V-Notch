#include "vnotch_common.h"
#include <algorithm>
#include <cstdio>

namespace vnotch::services
{

class AppVersion final
{
public:
    static std::u16string StripInformationalPrefixAndMetadata(std::u16string_view info_version)
    {
        size_t plus_pos = info_version.find(u'+');
        std::u16string_view core = (plus_pos == std::u16string_view::npos)
            ? info_version
            : info_version.substr(0, plus_pos);
        while (!core.empty() && (core.front() == u'v' || core.front() == u'V'))
        {
            core.remove_prefix(1);
        }
        return std::u16string(core);
    }

    static std::u16string FormatAssemblyVersion(
        uint8_t has_version,
        int32_t major,
        int32_t minor,
        int32_t build,
        int32_t revision)
    {
        if (!has_version)
        {
            return u"0.0.0";
        }
        int32_t clamped_build = std::max(0, build);
        char buf[64]{};
        int n = 0;
        if (revision > 0)
        {
            n = std::snprintf(buf, sizeof(buf), "%d.%d.%d.%d", major, minor, clamped_build, revision);
        }
        else
        {
            n = std::snprintf(buf, sizeof(buf), "%d.%d.%d", major, minor, clamped_build);
        }
        std::u16string out;
        if (n > 0)
        {
            out.resize(static_cast<size_t>(n));
            for (int i = 0; i < n; ++i)
            {
                out[static_cast<size_t>(i)] = static_cast<char16_t>(static_cast<unsigned char>(buf[i]));
            }
        }
        return out;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_app_version_strip_informational(
    const char16_t* info_ptr,
    int32_t info_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    std::u16string res = vnotch::services::AppVersion::StripInformationalPrefixAndMetadata(
        vnotch::make_u16_view(info_ptr, info_len));
    return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_app_version_format_assembly(
    uint8_t has_version,
    int32_t major,
    int32_t minor,
    int32_t build,
    int32_t revision,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    std::u16string res = vnotch::services::AppVersion::FormatAssemblyVersion(
        has_version, major, minor, build, revision);
    return vnotch::copy_out_u16(res, out_buf, out_cap, out_len);
}

} // extern "C"
