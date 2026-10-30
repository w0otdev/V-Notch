#include "../vnotch_native.h"

VN_EXPORT int32_t __stdcall vn_setup_show_message(const wchar_t* text, int32_t text_length,
    const wchar_t* title, int32_t title_length, int32_t mode, int32_t* out_result) noexcept
{
    if (!text || !title || !out_result || text_length < 0 || text_length > 32768 ||
        title_length < 0 || title_length > 32768 || mode < 0 || mode > 2)
        return VN_ERR_INVALID_ARG;
    *out_result = 0;
    try
    {
        // Own null-terminated copies at the Win32 boundary; caller buffers use lengths.
        const std::wstring message(text, static_cast<size_t>(text_length));
        const std::wstring caption(title, static_cast<size_t>(title_length));
        const UINT buttons = mode == 0 ? MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 :
            MB_OK | (mode == 1 ? MB_ICONINFORMATION : MB_ICONWARNING);
        *out_result = MessageBoxW(nullptr, message.c_str(), caption.c_str(), buttons | MB_SETFOREGROUND);
        return *out_result != 0 ? VN_OK : VN_ERR_FAIL;
    }
    catch (...) { return VN_ERR_FAIL; }
}
