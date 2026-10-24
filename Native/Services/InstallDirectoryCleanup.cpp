#include "vnotch_common.h"

namespace vnotch::services
{

class InstallDirectoryCleanup final
{
    static constexpr bool IsControlChar(char16_t ch) noexcept
    {
        return ch <= 0x001Fu || (ch >= 0x007Fu && ch <= 0x009Fu);
    }

    static constexpr bool IsInvalidFileNameChar(char16_t ch) noexcept
    {
        if (ch < 32u)
        {
            return true;
        }
        switch (ch)
        {
            case u'\"':
            case u'<':
            case u'>':
            case u'|':
            case u':':
            case u'*':
            case u'?':
            case u'\\':
            case u'/':
                return true;
            default:
                return false;
        }
    }

public:
    static bool HasValidInputSyntax(std::u16string_view directory) noexcept
    {
        if (vnotch::is_null_or_whitespace(directory))
        {
            return false;
        }
        for (char16_t ch : directory)
        {
            if (IsControlChar(ch))
            {
                return false;
            }
        }
        if (directory.size() >= 2 &&
            ((directory[0] == u'\\' && directory[1] == u'\\') ||
             (directory[0] == u'/' && directory[1] == u'/')))
        {
            return false;
        }
        // Fully qualified drive path check: [A-Za-z]:[\ or /]
        if (directory.size() < 3 ||
            !vnotch::is_ascii_alpha(directory[0]) ||
            directory[1] != u':' ||
            (directory[2] != u'\\' && directory[2] != u'/'))
        {
            return false;
        }
        return true;
    }

    static bool HasValidFullPathSegments(std::u16string_view full_path, std::u16string_view root) noexcept
    {
        if (root.size() != 3 || !vnotch::is_ascii_alpha(root[0]) || root[1] != u':')
        {
            return false;
        }
        std::u16string_view root_trimmed = root;
        while (!root_trimmed.empty() && (root_trimmed.back() == u'\\' || root_trimmed.back() == u'/'))
        {
            root_trimmed.remove_suffix(1);
        }
        if (vnotch::equals_ignore_case(full_path, root_trimmed) || full_path.size() <= 3)
        {
            return false;
        }

        std::u16string_view rest = full_path.substr(3);
        size_t i = 0;
        while (i < rest.size())
        {
            size_t sep = i;
            while (sep < rest.size() && rest[sep] != u'\\' && rest[sep] != u'/')
            {
                if (IsInvalidFileNameChar(rest[sep]))
                {
                    return false;
                }
                ++sep;
            }
            i = sep + 1;
        }
        return true;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_install_cleanup_has_valid_input_syntax(
    const char16_t* dir_ptr,
    int32_t dir_len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = (dir_ptr != nullptr &&
                  vnotch::services::InstallDirectoryCleanup::HasValidInputSyntax(
                      vnotch::make_u16_view(dir_ptr, dir_len))) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_install_cleanup_has_valid_full_path_segments(
    const char16_t* full_ptr,
    int32_t full_len,
    const char16_t* root_ptr,
    int32_t root_len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = (full_ptr != nullptr && root_ptr != nullptr &&
                  vnotch::services::InstallDirectoryCleanup::HasValidFullPathSegments(
                      vnotch::make_u16_view(full_ptr, full_len),
                      vnotch::make_u16_view(root_ptr, root_len))) ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
