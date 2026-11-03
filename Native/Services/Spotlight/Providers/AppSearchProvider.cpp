#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include "vnotch_common.h"
#include <string>
#include <vector>

namespace vnotch::services
{

class AppSearchProvider final
{
public:
    // Returns: 0 = Empty, 1 = UsePathDirectly, 2 = PrefixAppsFolderWithAppId, 3 = PrefixAppsFolderWithPath
    static int32_t ClassifyShellTarget(
        std::u16string_view path,
        std::u16string_view app_id,
        uint8_t is_unc,
        uint8_t exists_on_disk) noexcept
    {
        if (is_unc != 0)
        {
            return 0;
        }
        if (vnotch::starts_with_ignore_case(path, u"shell:") || exists_on_disk != 0)
        {
            return 1;
        }
        if (!app_id.empty())
        {
            return 2;
        }
        if (!path.empty())
        {
            return 3;
        }
        return 0;
    }

    static int32_t ScanStartMenu(
        std::u16string_view start_menu,
        char16_t* out_buf,
        int32_t out_cap,
        int32_t* out_written)
    {
        if (out_written == nullptr)
        {
            return VN_ERR_INVALID_ARG;
        }
        *out_written = 0;

        std::u16string_view trimmed = vnotch::trim_view(start_menu);
        while (!trimmed.empty() && (trimmed.back() == u'\\' || trimmed.back() == u'/'))
        {
            trimmed.remove_suffix(1);
        }
        if (trimmed.empty())
        {
            return VN_OK;
        }

        std::u16string programs;
        programs.reserve(trimmed.size() + 10);
        for (char16_t c : trimmed)
        {
            programs.push_back(c == u'/' ? u'\\' : c);
        }
        programs.append(u"\\Programs");

        DWORD root_attr = ::GetFileAttributesW(reinterpret_cast<LPCWSTR>(programs.c_str()));
        if (root_attr == INVALID_FILE_ATTRIBUTES || (root_attr & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            return VN_OK;
        }

        struct DirEntry
        {
            std::u16string path;
            int32_t depth;
        };

        std::vector<DirEntry> queue;
        queue.reserve(64);
        queue.push_back({std::move(programs), 0});

        int32_t written = 0;
        bool overflow = false;
        std::u16string pattern;
        pattern.reserve(300);

        for (size_t q_idx = 0; q_idx < queue.size(); ++q_idx)
        {
            std::u16string current_dir = std::move(queue[q_idx].path);
            int32_t current_depth = queue[q_idx].depth;

            pattern.assign(current_dir);
            pattern.append(u"\\*");

            WIN32_FIND_DATAW fd{};
            HANDLE h_find = ::FindFirstFileExW(
                reinterpret_cast<LPCWSTR>(pattern.c_str()),
                FindExInfoBasic,
                &fd,
                FindExSearchNameMatch,
                nullptr,
                FIND_FIRST_EX_LARGE_FETCH);
            if (h_find == INVALID_HANDLE_VALUE)
            {
                continue;
            }

            do
            {
                if (fd.cFileName[0] == L'.' &&
                    (fd.cFileName[1] == L'\0' || (fd.cFileName[1] == L'.' && fd.cFileName[2] == L'\0')))
                {
                    continue;
                }

                std::u16string_view name(reinterpret_cast<const char16_t*>(fd.cFileName));
                if (name.empty())
                {
                    continue;
                }

                if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    if (current_depth < 32 && (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                    {
                        std::u16string sub_dir;
                        sub_dir.reserve(current_dir.size() + 1 + name.size());
                        sub_dir.assign(current_dir);
                        sub_dir.push_back(u'\\');
                        sub_dir.append(name);
                        queue.push_back({std::move(sub_dir), current_depth + 1});
                    }
                    continue;
                }

                if (name.size() >= 4 && vn_equals_icase_ascii(name.substr(name.size() - 4), u".lnk"))
                {
                    std::u16string_view title = name.substr(0, name.size() - 4);
                    int32_t entry_len = static_cast<int32_t>(title.size() + 1 + current_dir.size() + 1 + name.size() + 1);
                    if (out_buf != nullptr && !overflow && written + entry_len <= out_cap)
                    {
                        std::memcpy(out_buf + written, title.data(), title.size() * sizeof(char16_t));
                        written += static_cast<int32_t>(title.size());
                        out_buf[written++] = u'\0';
                        std::memcpy(out_buf + written, current_dir.data(), current_dir.size() * sizeof(char16_t));
                        written += static_cast<int32_t>(current_dir.size());
                        out_buf[written++] = u'\\';
                        std::memcpy(out_buf + written, name.data(), name.size() * sizeof(char16_t));
                        written += static_cast<int32_t>(name.size());
                        out_buf[written++] = u'\0';
                    }
                    else
                    {
                        overflow = true;
                        written += entry_len;
                    }
                }
            } while (::FindNextFileW(h_find, &fd) != 0);

            ::FindClose(h_find);
        }

        *out_written = written;
        if (written == 0)
        {
            return VN_OK;
        }
        return (out_buf == nullptr || overflow || written > out_cap) ? VN_ERR_BUFFER_TOO_SMALL : VN_OK;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_app_search_provider_classify_target(
    const char16_t* path_ptr, int32_t path_len,
    const char16_t* app_id_ptr, int32_t app_id_len,
    uint8_t is_unc,
    uint8_t exists_on_disk,
    int32_t* out_mode) noexcept
{
    if (out_mode == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_mode = vnotch::services::AppSearchProvider::ClassifyShellTarget(
        vnotch::make_u16_view(path_ptr, path_len),
        vnotch::make_u16_view(app_id_ptr, app_id_len),
        is_unc,
        exists_on_disk);
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_app_search_provider_scan_start_menu(
    const char16_t* start_menu_ptr,
    int32_t start_menu_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_written) noexcept
{
    try
    {
        return vnotch::services::AppSearchProvider::ScanStartMenu(
            vnotch::make_u16_view(start_menu_ptr, start_menu_len),
            out_buf,
            out_cap,
            out_written);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

} // extern "C"
