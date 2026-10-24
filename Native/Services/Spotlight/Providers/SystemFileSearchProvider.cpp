#include "vnotch_common.h"

namespace vnotch::services
{

class SystemFileSearchProvider final
{
    static constexpr std::u16string_view kLaunchableExtensions[] = {
        u".cpl", u".msc", u".exe", u".com", u".cmd", u".bat"
    };

    static std::u16string_view GetFileName(std::u16string_view path) noexcept
    {
        for (size_t i = path.size(); i > 0; --i)
        {
            char16_t c = path[i - 1];
            if (c == u'\\' || c == u'/' || c == u':')
            {
                return path.substr(i);
            }
        }
        return path;
    }

    static std::u16string_view GetStem(std::u16string_view name) noexcept
    {
        for (size_t i = name.size(); i > 0; --i)
        {
            if (name[i - 1] == u'.')
            {
                return name.substr(0, i - 1);
            }
        }
        return name;
    }

public:
    static bool IsLaunchableExtension(std::u16string_view ext) noexcept
    {
        for (std::u16string_view allowed : kLaunchableExtensions)
        {
            if (vnotch::equals_ignore_case(ext, allowed))
            {
                return true;
            }
        }
        return false;
    }

    static double ScoreSystemFile(
        int32_t kind,
        std::u16string_view target,
        std::u16string_view requested) noexcept
    {
        // SpotlightResultKind.Application == 0
        if (kind != 0 || requested.empty())
        {
            return 0.0;
        }
        std::u16string_view name = GetFileName(target);
        std::u16string_view stem = GetStem(name);
        if (vnotch::equals_ignore_case(name, requested) ||
            vnotch::equals_ignore_case(stem, requested) ||
            vnotch::equals_ignore_case(target, requested))
        {
            return 1200.0;
        }
        if (vnotch::starts_with_ignore_case(stem, requested))
        {
            return 140.0;
        }
        if (requested.size() >= 3 && vnotch::contains_ignore_case(stem, requested))
        {
            return 80.0;
        }
        return 0.0;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_system_file_search_is_launchable_ext(
    const char16_t* ext_ptr,
    int32_t ext_len,
    uint8_t* out_launchable) noexcept
{
    if (out_launchable == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_launchable = vnotch::services::SystemFileSearchProvider::IsLaunchableExtension(
        vnotch::make_u16_view(ext_ptr, ext_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_system_file_search_score(
    int32_t kind,
    const char16_t* target_ptr,
    int32_t target_len,
    const char16_t* req_ptr,
    int32_t req_len,
    double* out_score) noexcept
{
    if (out_score == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_score = vnotch::services::SystemFileSearchProvider::ScoreSystemFile(
        kind,
        vnotch::make_u16_view(target_ptr, target_len),
        vnotch::make_u16_view(req_ptr, req_len));
    return VN_OK;
}

} // extern "C"
