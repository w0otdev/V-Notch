#include "vnotch_common.h"
#include <algorithm>
#include <cstdio>

namespace vnotch::services
{

class AppVersion final
{
public:
    static std::u16string_view StripInformationalPrefixAndMetadata(std::u16string_view info_version) noexcept
    {
        size_t plus_pos = info_version.find(u'+');
        std::u16string_view core = (plus_pos == std::u16string_view::npos)
            ? info_version
            : info_version.substr(0, plus_pos);
        while (!core.empty() && (core.front() == u'v' || core.front() == u'V'))
        {
            core.remove_prefix(1);
        }
        return core;
    }

    static size_t FormatAssemblyVersion(
        uint8_t has_version,
        int32_t major,
        int32_t minor,
        int32_t build,
        int32_t revision,
        char16_t (&out)[64]) noexcept
    {
        if (!has_version)
        {
            constexpr std::u16string_view kDefault = u"0.0.0";
            for (size_t i = 0; i < kDefault.size(); ++i) out[i] = kDefault[i];
            return kDefault.size();
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
        if (n <= 0)
        {
            return 0;
        }
        const int clamped_n = std::min<int>(n, static_cast<int>(sizeof(buf) - 1));
        for (int i = 0; i < clamped_n; ++i)
        {
            out[static_cast<size_t>(i)] = static_cast<char16_t>(static_cast<unsigned char>(buf[i]));
        }
        return static_cast<size_t>(clamped_n);
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
    std::u16string_view res = vnotch::services::AppVersion::StripInformationalPrefixAndMetadata(
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
    char16_t res[64]{};
    size_t len = vnotch::services::AppVersion::FormatAssemblyVersion(
        has_version, major, minor, build, revision, res);
    return vnotch::copy_out_u16(std::u16string_view(res, len), out_buf, out_cap, out_len);
}

} // extern "C"
