#include "vnotch_common.h"
#include "vnotch_handle_registry.h"
#include <stdexcept>

VN_EXPORT int32_t vn_clipboard_validate_reparse_point(const char16_t*, int32_t, uint32_t, int32_t*, int32_t*) noexcept;
namespace {
struct Scan { std::vector<VnClipboardDirectoryRow> rows; std::u16string text; };
vnotch::HandleRegistry<Scan> scans;
struct Search { HANDLE handle; ~Search() { if (handle != INVALID_HANDLE_VALUE) FindClose(handle); } };
struct Entry { std::wstring name; WIN32_FIND_DATAW data; };
}
VN_EXPORT int32_t vn_clipboard_directory_scan(const char16_t* root, int32_t length, const char16_t* excluded, int32_t excluded_length, int32_t limit,
    int64_t* handle, const VnClipboardDirectoryRow** rows, int32_t* count, const char16_t** text, int32_t* win32_error) noexcept {
    if (!handle || !rows || !count || !text || !win32_error) return VN_ERR_INVALID_ARG;
    *handle = 0; *rows = nullptr; *count = 0; *text = nullptr; *win32_error = 0;
    if (!root || length <= 0 || excluded_length < 0 || (!excluded && excluded_length) || limit <= 0 || limit > 100001) return VN_ERR_INVALID_ARG;
    try {
        std::wstring initial(reinterpret_cast<const wchar_t*>(root), length);
        if (initial.find(L'\0') != initial.npos) return VN_ERR_INVALID_ARG;
        std::vector<std::wstring> pending{initial};
        auto result = std::make_unique<Scan>();
        while (!pending.empty()) {
            auto directory = std::move(pending.back()); pending.pop_back();
            // Validate again immediately before enumeration, including queued subdirectories.
            auto attributes = GetFileAttributesW(directory.c_str());
            if (attributes == INVALID_FILE_ATTRIBUTES) { *win32_error = GetLastError(); return VN_ERR_FAIL; }
            if (attributes & FILE_ATTRIBUTE_REPARSE_POINT) {
                int32_t kind = 0, error = 0;
                if (vn_clipboard_validate_reparse_point(reinterpret_cast<const char16_t*>(directory.data()), static_cast<int32_t>(directory.size()), attributes, &kind, &error) != VN_OK || kind != 0) {
                    *win32_error = error ? error : ERROR_ACCESS_DENIED; return VN_ERR_FAIL;
                }
            }
            auto pattern = directory + (directory.ends_with(L'\\') ? L"*" : L"\\*");
            WIN32_FIND_DATAW data{};
            Search search{FindFirstFileExW(pattern.c_str(), FindExInfoBasic, &data, FindExSearchNameMatch, nullptr, FIND_FIRST_EX_LARGE_FETCH)};
            if (search.handle == INVALID_HANDLE_VALUE) {
                const auto error = GetLastError(); if (error == ERROR_FILE_NOT_FOUND) continue;
                *win32_error = error; return VN_ERR_FAIL;
            }
            std::vector<Entry> children;
            do {
                std::wstring_view name(data.cFileName);
                if (name == L"." || name == L"..") continue;
                children.push_back({std::wstring(name), data});
                if (children.size() + result->rows.size() > static_cast<size_t>(limit)) return -4;
            } while (FindNextFileW(search.handle, &data));
            if (GetLastError() != ERROR_NO_MORE_FILES) { *win32_error = GetLastError(); return VN_ERR_FAIL; }
            std::sort(children.begin(), children.end(), [](auto& a, auto& b) { return a.name < b.name; });
            for (const auto& child : children) {
                auto path = directory + (directory.ends_with(L'\\') ? L"" : L"\\") + child.name;
                if (excluded_length > 0 && CompareStringOrdinal(path.data(), static_cast<int>(path.size()), reinterpret_cast<const wchar_t*>(excluded), excluded_length, TRUE) == CSTR_EQUAL) continue;
                auto view = std::u16string_view(reinterpret_cast<const char16_t*>(path.data()), path.size());
                if (child.data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) {
                    int32_t kind = 0, error = 0;
                    if (vn_clipboard_validate_reparse_point(view.data(), static_cast<int32_t>(view.size()), child.data.dwFileAttributes, &kind, &error) != VN_OK || kind != 0) {
                        *win32_error = error ? error : ERROR_ACCESS_DENIED; return VN_ERR_FAIL;
                    }
                }
                if (result->text.size() + view.size() > 32 * 1024 * 1024) return -4;
                const auto size = (static_cast<uint64_t>(child.data.nFileSizeHigh) << 32) | child.data.nFileSizeLow;
                const auto date = (static_cast<uint64_t>(child.data.ftLastWriteTime.dwHighDateTime) << 32) | child.data.ftLastWriteTime.dwLowDateTime;
                result->rows.push_back({static_cast<int64_t>(size), static_cast<int64_t>(date), static_cast<int32_t>(result->text.size()), static_cast<int32_t>(view.size()), child.data.dwFileAttributes, 0});
                result->text += view;
                if (child.data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) pending.push_back(std::move(path));
            }
        }
        *rows = result->rows.data(); *count = static_cast<int32_t>(result->rows.size()); *text = result->text.data();
        *handle = scans.Add(std::move(result)); return VN_OK;
    } catch (...) { return VN_ERR_FAIL; }
}
VN_EXPORT int32_t vn_clipboard_directory_scan_close(int64_t handle) noexcept { return scans.Remove(handle) ? VN_OK : VN_ERR_INVALID_ARG; }
