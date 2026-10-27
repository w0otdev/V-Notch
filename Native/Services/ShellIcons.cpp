#include "../vnotch_common.h"
#include <shellapi.h>
#include <shobjidl.h>
#include <commctrl.h>
#include <string>
#include <cstdint>

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "uuid.lib")

// Shell icon and thumbnail extraction. These return GDI handles (HICON/HBITMAP) as int64_t;
// the caller converts them to WPF bitmaps and releases them with vn_win32_destroy_icon /
// vn_win32_delete_gdi_object.

namespace vnotch::services::shellicons {

constexpr int32_t kMaxPathChars = 32768;

inline int64_t FromHandle(const void* handle) noexcept {
    return static_cast<int64_t>(reinterpret_cast<intptr_t>(handle));
}

bool CopyPath(const char16_t* path, int32_t length, std::wstring& out) noexcept {
    if (!path || length <= 0 || length >= kMaxPathChars) return false;
    try {
        out.assign(reinterpret_cast<const wchar_t*>(path), static_cast<size_t>(length));
        return true;
    } catch (...) {
        return false;
    }
}

// Joins the COM apartment of the calling thread when it has none; mode conflicts are harmless here.
struct ComScope {
    bool initialized;
    ComScope() noexcept {
        const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        initialized = SUCCEEDED(hr);
    }
    ComScope(const ComScope&) = delete;
    ComScope& operator=(const ComScope&) = delete;
    ~ComScope() {
        if (initialized) CoUninitialize();
    }
};

} // namespace vnotch::services::shellicons

namespace icons = vnotch::services::shellicons;

extern "C" {

// SHGetFileInfoW(SHGFI_ICON | flags). path may be an extension when SHGFI_USEFILEATTRIBUTES is set.
VN_EXPORT int32_t __stdcall vn_shell_get_file_info_icon(
    const char16_t* path, int32_t path_length, uint32_t file_attributes, uint32_t flags, int64_t* out_icon) noexcept {
    if (!out_icon) return VN_ERR_INVALID_ARG;
    *out_icon = 0;
    std::wstring wide;
    if (!icons::CopyPath(path, path_length, wide)) return VN_ERR_INVALID_ARG;
    icons::ComScope com;
    SHFILEINFOW info{};
    const DWORD_PTR result = SHGetFileInfoW(wide.c_str(), file_attributes, &info, sizeof(info), SHGFI_ICON | flags);
    *out_icon = icons::FromHandle(info.hIcon);
    return (result != 0 && info.hIcon != nullptr) ? VN_OK : VN_ERR_FAIL;
}

// Icon from the system image list without the shortcut overlay (used for .lnk files).
VN_EXPORT int32_t __stdcall vn_shell_get_base_icon(
    const char16_t* path, int32_t path_length, uint8_t small_icon, int64_t* out_icon) noexcept {
    if (!out_icon) return VN_ERR_INVALID_ARG;
    *out_icon = 0;
    std::wstring wide;
    if (!icons::CopyPath(path, path_length, wide)) return VN_ERR_INVALID_ARG;
    icons::ComScope com;
    SHFILEINFOW info{};
    const DWORD_PTR image_list = SHGetFileInfoW(
        wide.c_str(), 0, &info, sizeof(info), SHGFI_SYSICONINDEX | (small_icon != 0 ? SHGFI_SMALLICON : SHGFI_LARGEICON));
    if (image_list == 0) return VN_ERR_FAIL;
    HICON icon = ImageList_GetIcon(reinterpret_cast<HIMAGELIST>(image_list), info.iIcon, ILD_TRANSPARENT);
    *out_icon = icons::FromHandle(icon);
    return icon != nullptr ? VN_OK : VN_ERR_FAIL;
}

VN_EXPORT int32_t __stdcall vn_shell_extract_icon(
    const char16_t* path, int32_t path_length, int32_t icon_index, uint8_t small_icon, int64_t* out_icon) noexcept {
    if (!out_icon) return VN_ERR_INVALID_ARG;
    *out_icon = 0;
    std::wstring wide;
    if (!icons::CopyPath(path, path_length, wide)) return VN_ERR_INVALID_ARG;
    HICON large_icon = nullptr;
    HICON small_icon_handle = nullptr;
    const UINT count = ExtractIconExW(wide.c_str(), icon_index, small_icon != 0 ? nullptr : &large_icon, small_icon != 0 ? &small_icon_handle : nullptr, 1);
    HICON icon = small_icon != 0 ? small_icon_handle : large_icon;
    if (count == 0 || icon == nullptr) return VN_ERR_FAIL;
    *out_icon = icons::FromHandle(icon);
    return VN_OK;
}

// IShellItemImageFactory::GetImage for the item at path. flags are SIIGBF_* values.
VN_EXPORT int32_t __stdcall vn_shell_get_thumbnail_bitmap(
    const char16_t* path, int32_t path_length, int32_t size, uint32_t flags, int64_t* out_bitmap) noexcept {
    if (!out_bitmap || size <= 0) return VN_ERR_INVALID_ARG;
    *out_bitmap = 0;
    std::wstring wide;
    if (!icons::CopyPath(path, path_length, wide)) return VN_ERR_INVALID_ARG;
    icons::ComScope com;
    IShellItemImageFactory* factory = nullptr;
    if (FAILED(SHCreateItemFromParsingName(wide.c_str(), nullptr, IID_PPV_ARGS(&factory))) || factory == nullptr)
        return VN_ERR_FAIL;
    HBITMAP bitmap = nullptr;
    const HRESULT hr = factory->GetImage(SIZE{size, size}, static_cast<SIIGBF>(flags), &bitmap);
    factory->Release();
    if (FAILED(hr) || bitmap == nullptr) return VN_ERR_FAIL;
    *out_bitmap = icons::FromHandle(bitmap);
    return VN_OK;
}

} // extern "C"
