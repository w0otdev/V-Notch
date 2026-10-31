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

        static constexpr std::u16string_view strip_verbatim_prefix(std::u16string_view p) noexcept
        {
            if (p.size() >= 4 && p[0] == u'\\' && p[1] == u'\\' && p[2] == u'?' && p[3] == u'\\')
            {
                return p.substr(4);
            }
            return p;
        }

        static int32_t vn_clipboard_is_cloud_placeholder_impl(uint32_t tag, uint8_t* out_is_cloud) noexcept
        {
            try
            {
                if (out_is_cloud == nullptr)
                {
                    return VN_ERR_INVALID_ARG;
                }
                *out_is_cloud = is_cloud_placeholder_tag(tag) ? 1u : 0u;
                return VN_OK;
            }
            catch (...)
            {
                return VN_ERR_FAIL;
            }
        }

        static int32_t vn_clipboard_validate_reparse_point_impl(const char16_t* path_ptr,
            int32_t path_len,
            uint32_t file_attributes,
            int32_t* out_error_kind,
            int32_t* out_win32_error) noexcept
        {
            try
            {
                if (out_error_kind == nullptr || out_win32_error == nullptr)
                {
                    return VN_ERR_INVALID_ARG;
                }
                *out_error_kind = 0;
                *out_win32_error = 0;

                std::u16string_view sv = make_u16_view(path_ptr, path_len);
                std::wstring wpath(reinterpret_cast<const wchar_t*>(sv.data()), sv.size());

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
            catch (...)
            {
                return VN_ERR_FAIL;
            }
        }

        static int32_t vn_clipboard_validate_file_handle_impl(
            intptr_t file_handle,
            const char16_t* expected_full_path_ptr,
            int32_t expected_full_path_len,
            int32_t* out_error_kind,
            int32_t* out_win32_error) noexcept
        {
            try
            {
                if (out_error_kind == nullptr || out_win32_error == nullptr || expected_full_path_len < 0 ||
                    (expected_full_path_len > 0 && expected_full_path_ptr == nullptr))
                {
                    return VN_ERR_INVALID_ARG;
                }
                *out_error_kind = 0;
                *out_win32_error = 0;

                HANDLE h = reinterpret_cast<HANDLE>(file_handle);
                if (h == nullptr || h == INVALID_HANDLE_VALUE)
                {
                    return VN_ERR_INVALID_ARG;
                }

                BY_HANDLE_FILE_INFORMATION file_info{};
                if (!GetFileInformationByHandle(h, &file_info))
                {
                    *out_error_kind = 1;
                    *out_win32_error = static_cast<int32_t>(GetLastError());
                    return VN_OK;
                }

                if ((file_info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0u)
                {
                    *out_error_kind = 4;
                    return VN_OK;
                }

                bool is_hydrated_cloud = false;
                if ((file_info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0u)
                {
                    VnFileAttributeTagInfo tag_info{};
                    if (!GetFileInformationByHandleEx(h, static_cast<FILE_INFO_BY_HANDLE_CLASS>(9), &tag_info, sizeof(tag_info)))
                    {
                        *out_error_kind = 1;
                        *out_win32_error = static_cast<int32_t>(GetLastError());
                        return VN_OK;
                    }

                    if (!is_cloud_placeholder_tag(tag_info.reparse_tag))
                    {
                        *out_error_kind = 2;
                        return VN_OK;
                    }

                    constexpr uint32_t DEHYDRATED_MASK = 0x00441000u;
                    if ((tag_info.file_attributes & DEHYDRATED_MASK) != 0u || (file_info.dwFileAttributes & DEHYDRATED_MASK) != 0u)
                    {
                        *out_error_kind = 3;
                        return VN_OK;
                    }

                    is_hydrated_cloud = true;
                }

                wchar_t final_buf[4096];
                DWORD final_len = GetFinalPathNameByHandleW(h, final_buf, 4096, 0);
                if (final_len == 0 || final_len >= 4096)
                {
                    *out_error_kind = 1;
                    *out_win32_error = static_cast<int32_t>(final_len >= 4096 ? ERROR_FILENAME_EXCED_RANGE : GetLastError());
                    return VN_OK;
                }

                std::u16string_view final_sv(reinterpret_cast<const char16_t*>(final_buf), static_cast<size_t>(final_len));
                if (starts_with_ignore_case(final_sv, u"\\\\?\\UNC\\"))
                {
                    *out_error_kind = 2;
                    return VN_OK;
                }

                std::u16string_view expected_sv = make_u16_view(expected_full_path_ptr, expected_full_path_len);
                std::u16string_view clean_final = strip_verbatim_prefix(final_sv);
                std::u16string_view clean_expected = strip_verbatim_prefix(expected_sv);

                if (clean_final.empty() || clean_expected.empty() ||
                    (clean_final.size() >= 2 && clean_final[0] == u'\\' && clean_final[1] == u'\\') ||
                    CompareStringOrdinal(
                        reinterpret_cast<LPCWCH>(clean_final.data()),
                        static_cast<int>(clean_final.size()),
                        reinterpret_cast<LPCWCH>(clean_expected.data()),
                        static_cast<int>(clean_expected.size()),
                        TRUE) != CSTR_EQUAL)
                {
                    *out_error_kind = 2;
                    return VN_OK;
                }

                *out_error_kind = is_hydrated_cloud ? 5 : 0;
                return VN_OK;
            }
            catch (...)
            {
                return VN_ERR_FAIL;
            }
        }
    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_clipboard_is_cloud_placeholder(uint32_t tag, uint8_t* out_is_cloud) noexcept
{
    try
    {
        return vnotch::services::ClipboardImportPaths::vn_clipboard_is_cloud_placeholder_impl(tag, out_is_cloud);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_validate_reparse_point(const char16_t* path_ptr,
    int32_t path_len,
    uint32_t file_attributes,
    int32_t* out_error_kind,
    int32_t* out_win32_error) noexcept
{
    try
    {
        return vnotch::services::ClipboardImportPaths::vn_clipboard_validate_reparse_point_impl(path_ptr, path_len, file_attributes, out_error_kind, out_win32_error);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_clipboard_validate_file_handle(
    intptr_t file_handle,
    const char16_t* expected_full_path_ptr,
    int32_t expected_full_path_len,
    int32_t* out_error_kind,
    int32_t* out_win32_error) noexcept
{
    try
    {
        return vnotch::services::ClipboardImportPaths::vn_clipboard_validate_file_handle_impl(
            file_handle, expected_full_path_ptr, expected_full_path_len, out_error_kind, out_win32_error);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

