#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class ClipboardImportPaths final
    {
    public:
        static constexpr bool is_cloud_placeholder_tag(uint32_t tag) noexcept
        {
            return (tag & 0xFFFF0FFFu) == 0x9000001Au || tag == 0x80000021u || tag == 0x80000015u;
        }
        
        struct VnFileAttributeTagInfo
        {
            uint32_t file_attributes;
            uint32_t reparse_tag;
        };

        static int32_t vn_clipboard_is_cloud_placeholder_impl(uint32_t tag, uint8_t* out_is_cloud) noexcept
        {
    if (out_is_cloud == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_is_cloud = is_cloud_placeholder_tag(tag) ? 1u : 0u;
    return VN_OK;
}

        static int32_t vn_clipboard_validate_reparse_point_impl(const char16_t* path_ptr,
    int32_t path_len,
    uint32_t file_attributes,
    int32_t* out_error_kind,
    int32_t* out_win32_error) noexcept
        {
    if (out_error_kind == nullptr || out_win32_error == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_error_kind = 0;
    *out_win32_error = 0;

    std::u16string_view sv = make_u16_view(path_ptr, path_len);
    std::wstring wpath;
    try { wpath.assign(reinterpret_cast<const wchar_t*>(sv.data()), sv.size()); } catch (...) { return VN_ERR_FAIL; }

    HANDLE h = CreateFileW(
        wpath.c_str(),
        0,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        0x02200000u,
        nullptr);

    if (h == INVALID_HANDLE_VALUE)
    {
        *out_error_kind = 1;
        *out_win32_error = static_cast<int32_t>(GetLastError());
        return VN_OK;
    }

    VnFileAttributeTagInfo info{};
    BOOL ok = GetFileInformationByHandleEx(h, static_cast<FILE_INFO_BY_HANDLE_CLASS>(9), &info, sizeof(info));
    DWORD err = ok ? 0 : GetLastError();
    CloseHandle(h);

    if (!ok)
    {
        *out_error_kind = 1;
        *out_win32_error = static_cast<int32_t>(err);
        return VN_OK;
    }

    if (!is_cloud_placeholder_tag(info.reparse_tag))
    {
        *out_error_kind = 2;
        return VN_OK;
    }

    constexpr uint32_t DEHYDRATED_MASK = 0x00441000u;
    if ((info.file_attributes & DEHYDRATED_MASK) != 0u || (file_attributes & DEHYDRATED_MASK) != 0u)
    {
        *out_error_kind = 3;
        return VN_OK;
    }

    return VN_OK;
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_clipboard_is_cloud_placeholder(uint32_t tag, uint8_t* out_is_cloud) noexcept
{
    return vnotch::services::ClipboardImportPaths::vn_clipboard_is_cloud_placeholder_impl(tag, out_is_cloud);
}

VN_EXPORT int32_t vn_clipboard_validate_reparse_point(const char16_t* path_ptr,
    int32_t path_len,
    uint32_t file_attributes,
    int32_t* out_error_kind,
    int32_t* out_win32_error) noexcept
{
    return vnotch::services::ClipboardImportPaths::vn_clipboard_validate_reparse_point_impl(path_ptr, path_len, file_attributes, out_error_kind, out_win32_error);
}

