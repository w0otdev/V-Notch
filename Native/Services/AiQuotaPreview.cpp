#include "../vnotch_native.h"
#include <sddl.h>
#include <memory>

#pragma comment(lib, "advapi32.lib")

namespace
{
    constexpr wchar_t kMessageName[] = L"VNotch.QuotaPreview.v1";
    struct LocalStringDeleter
    {
        void operator()(wchar_t* value) const noexcept { LocalFree(value); }
    };

    // Only windows registered by the same Windows user in this desktop are eligible.
    bool Marker(std::wstring& name)
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
        alignas(TOKEN_USER) BYTE storage[256]{};
        DWORD written = 0;
        const BOOL read = GetTokenInformation(token, TokenUser, storage, sizeof(storage), &written);
        CloseHandle(token);
        if (!read) return false;
        wchar_t* sid = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage)->User.Sid, &sid)) return false;
        const std::unique_ptr<wchar_t, LocalStringDeleter> owned(sid);
        name = L"VNotch.QuotaPreview.";
        name += sid;
        return true;
    }

    struct PreviewRequest
    {
        const wchar_t* marker;
        UINT message;
        int32_t threshold;
        int32_t provider;
        bool accepted = false;
    };

    BOOL CALLBACK SendPreview(HWND window, LPARAM parameter) noexcept
    {
        auto& request = *reinterpret_cast<PreviewRequest*>(parameter);
        if (!GetPropW(window, request.marker)) return TRUE;
        DWORD_PTR result = 0;
        if (SendMessageTimeoutW(window, request.message, request.threshold, request.provider,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, &result) && result == 1)
        {
            request.accepted = true;
            return FALSE;
        }
        return TRUE;
    }
}

VN_EXPORT int32_t __stdcall vn_ai_quota_preview_register(intptr_t window, uint32_t* out_message) noexcept
{
    if (!out_message) return VN_ERR_INVALID_ARG;
    *out_message = 0;
    const auto hwnd = reinterpret_cast<HWND>(window);
    DWORD owner = 0;
    GetWindowThreadProcessId(hwnd, &owner);
    if (owner != GetCurrentProcessId()) return VN_ERR_INVALID_ARG;
    try
    {
        std::wstring marker;
        const UINT message = RegisterWindowMessageW(kMessageName);
        if (!message || !Marker(marker) || !SetPropW(hwnd, marker.c_str(), reinterpret_cast<HANDLE>(1)))
            return VN_ERR_FAIL;
        *out_message = message;
        return VN_OK;
    }
    catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_ai_quota_preview_unregister(intptr_t window) noexcept
{
    try
    {
        std::wstring marker;
        if (Marker(marker)) RemovePropW(reinterpret_cast<HWND>(window), marker.c_str());
        return VN_OK;
    }
    catch (...) { return VN_ERR_FAIL; }
}

VN_EXPORT int32_t __stdcall vn_ai_quota_preview_send(
    const wchar_t* threshold_ptr, int32_t threshold_len,
    const wchar_t* provider_ptr, int32_t provider_len) noexcept
{
    if (!threshold_ptr || threshold_len != 2 || !provider_ptr || provider_len < 1 || provider_len > 6)
        return VN_ERR_INVALID_ARG;
    const std::wstring_view value(threshold_ptr, static_cast<size_t>(threshold_len));
    const int32_t threshold = value == L"50" ? 50 : value == L"20" ? 20 : 0;
    int32_t provider = -1;
    if (CompareStringOrdinal(provider_ptr, provider_len, L"Codex", 5, TRUE) == CSTR_EQUAL) provider = 0;
    if (CompareStringOrdinal(provider_ptr, provider_len, L"Claude", 6, TRUE) == CSTR_EQUAL) provider = 1;
    if (!threshold || provider < 0) return VN_ERR_INVALID_ARG;
    try
    {
        std::wstring marker;
        const UINT message = RegisterWindowMessageW(kMessageName);
        if (!message || !Marker(marker)) return VN_ERR_FAIL;
        PreviewRequest request{ marker.c_str(), message, threshold, provider };
        EnumWindows(SendPreview, reinterpret_cast<LPARAM>(&request));
        return request.accepted ? VN_OK : VN_ERR_FAIL;
    }
    catch (...) { return VN_ERR_FAIL; }
}
