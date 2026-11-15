#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

VN_EXPORT int32_t __stdcall vn_qwen_translation_try_read_output(
    const wchar_t* json_ptr, int32_t json_len,
    wchar_t* out_buf, int32_t out_cap, int32_t* out_len, uint8_t* out_ok) noexcept
{
    try
    {
        if (out_len == nullptr || out_ok == nullptr || json_len < 0)
            return VN_ERR_INVALID_ARG;
        *out_len = 0;
        *out_ok = 0;

        const std::u16string_view src = make_u16_view(json_ptr, json_len);
        JsonSpan root{};
        if (!json_parse_document(src, root) || root.kind != JsonKind::Object)
            return VN_OK;

        size_t pos = 1;
        skip_json_ws(root.raw, pos);
        if (pos >= root.raw.size() || root.raw[pos] == u'}')
            return VN_OK;

        std::u16string key;
        if (!scan_json_string(root.raw, pos, &key) || key != u"translation")
            return VN_OK;
        skip_json_ws(root.raw, pos);
        if (pos >= root.raw.size() || root.raw[pos] != u':')
            return VN_OK;
        ++pos;

        JsonSpan val{};
        if (!json_parse_value(root.raw, pos, val, 1) || val.kind != JsonKind::String)
            return VN_OK;
        skip_json_ws(root.raw, pos);
        if (pos >= root.raw.size() || root.raw[pos] != u'}')
            return VN_OK;

        std::u16string decoded;
        if (!json_decode_string(val, decoded))
            return VN_OK;

        const std::u16string_view trimmed = vnotch::trim_view(decoded);
        *out_ok = 1;
        return write_wstr_out(trimmed, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

