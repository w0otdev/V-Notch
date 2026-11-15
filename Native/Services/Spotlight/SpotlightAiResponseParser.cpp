#include "Parsing/Utf16Text.h"
#include "Parsing/Utf16Json.h"

using namespace vnotch::parsing;

VN_EXPORT int32_t __stdcall vn_spotlight_ai_extract_response_text(
    const wchar_t* json_ptr, int32_t json_len, int32_t provider_kind,
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

        std::u16string result;
        auto iter_array = [](JsonSpan arr, auto&& fn) -> bool
        {
            if (arr.kind != JsonKind::Array || arr.raw.size() < 2) return false;
            size_t pos = 1;
            skip_json_ws(arr.raw, pos);
            if (pos < arr.raw.size() && arr.raw[pos] == u']') return true;
            size_t idx = 0;
            while (pos < arr.raw.size())
            {
                JsonSpan elem{};
                if (!json_parse_value(arr.raw, pos, elem, 1)) return false;
                if (!fn(idx++, elem)) return false;
                skip_json_ws(arr.raw, pos);
                if (pos >= arr.raw.size()) return false;
                if (arr.raw[pos] == u']') return true;
                if (arr.raw[pos] != u',') return false;
                ++pos;
            }
            return false;
        };

        if (provider_kind == 1) // Gemini
        {
            JsonSpan candidates{};
            if (!json_try_get_property(root, u"candidates", candidates) || candidates.kind != JsonKind::Array)
                return VN_OK;

            JsonSpan first_cand{};
            bool has_first = false;
            if (!iter_array(candidates, [&](size_t idx, JsonSpan elem) -> bool
            {
                if (idx == 0)
                {
                    first_cand = elem;
                    has_first = true;
                }
                return true;
            }) || !has_first || first_cand.kind != JsonKind::Object)
            {
                return VN_OK;
            }

            JsonSpan content{}, parts{};
            if (!json_try_get_property(first_cand, u"content", content) || content.kind != JsonKind::Object ||
                !json_try_get_property(content, u"parts", parts) || parts.kind != JsonKind::Array)
            {
                return VN_OK;
            }

            std::u16string piece;
            if (!iter_array(parts, [&](size_t, JsonSpan p) -> bool
            {
                if (p.kind != JsonKind::Object) return false;
                JsonSpan text_prop{};
                if (json_try_get_property(p, u"text", text_prop))
                {
                    JsonSpan thought{};
                    if (!json_try_get_property(p, u"thought", thought) || thought.kind != JsonKind::True)
                    {
                        if (text_prop.kind == JsonKind::String)
                        {
                            if (!json_decode_string(text_prop, piece)) return false;
                            result.append(piece);
                        }
                        else if (text_prop.kind != JsonKind::Null)
                        {
                            return false;
                        }
                    }
                }
                return true;
            }))
            {
                return VN_OK;
            }
        }
        else if (provider_kind == 2) // Claude
        {
            JsonSpan content{};
            if (!json_try_get_property(root, u"content", content) || content.kind != JsonKind::Array)
                return VN_OK;

            std::u16string type_str, piece;
            if (!iter_array(content, [&](size_t, JsonSpan p) -> bool
            {
                if (p.kind != JsonKind::Object) return false;
                JsonSpan type_prop{};
                if (!json_try_get_property(p, u"type", type_prop)) return false;
                if (type_prop.kind == JsonKind::Null) return true;
                if (type_prop.kind != JsonKind::String || !json_decode_string(type_prop, type_str))
                    return false;
                if (type_str == u"text")
                {
                    JsonSpan text_prop{};
                    if (!json_try_get_property(p, u"text", text_prop)) return false;
                    if (text_prop.kind == JsonKind::String)
                    {
                        if (!json_decode_string(text_prop, piece)) return false;
                        result.append(piece);
                    }
                    else if (text_prop.kind != JsonKind::Null)
                    {
                        return false;
                    }
                }
                return true;
            }))
            {
                return VN_OK;
            }
        }
        else // OpenAI / DeepSeek / Default
        {
            JsonSpan choices{};
            if (!json_try_get_property(root, u"choices", choices) || choices.kind != JsonKind::Array)
                return VN_OK;

            JsonSpan first_choice{};
            bool has_first = false;
            if (!iter_array(choices, [&](size_t idx, JsonSpan elem) -> bool
            {
                if (idx == 0)
                {
                    first_choice = elem;
                    has_first = true;
                }
                return true;
            }) || !has_first || first_choice.kind != JsonKind::Object)
            {
                return VN_OK;
            }

            JsonSpan message{}, content{};
            if (!json_try_get_property(first_choice, u"message", message) || message.kind != JsonKind::Object ||
                !json_try_get_property(message, u"content", content))
            {
                return VN_OK;
            }
            if (content.kind == JsonKind::Null) return VN_OK;
            if (content.kind != JsonKind::String || !json_decode_string(content, result))
                return VN_OK;
        }

        if (vnotch::trim_view(result).empty()) return VN_OK;
        *out_ok = 1;
        return write_wstr_out(result, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        return VN_ERR_FAIL;
    }
}

