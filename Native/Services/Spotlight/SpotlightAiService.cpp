#include "vnotch_common.h"
#include "../../vnotch_json_view.h"
#include <algorithm>
#include <climits>
#include <cstdio>

namespace vnotch::services
{

class SpotlightAiService final
{
public:
    static bool ValidateApiKey(std::u16string_view key) noexcept
    {
        std::u16string_view trimmed = vnotch::trim_u16(key);
        if (trimmed.empty() || trimmed.size() > 4096)
        {
            return false;
        }
        for (char16_t c : trimmed)
        {
            if (c < u'!' || c > u'~')
            {
                return false;
            }
        }
        return true;
    }

    static void ResolveProviderError(
        int32_t code,
        bool streaming,
        std::u16string_view& out_key,
        char16_t (&diag_buf)[64],
        std::u16string_view& out_diag) noexcept
    {
        if (code == 401) out_key = u"spotlight.ai.authError";
        else if (code == 403) out_key = u"spotlight.ai.permissionError";
        else if (code == 402) out_key = u"spotlight.ai.billingError";
        else if (code == 408 || code == 504) out_key = u"spotlight.ai.requestTimeout";
        else if (code == 413) out_key = u"spotlight.ai.contextError";
        else if (code == 429) out_key = u"spotlight.ai.rateError";
        else if (code == 400 || code == 422) out_key = u"spotlight.ai.requestError";
        else if (code == 404) out_key = u"spotlight.ai.modelError";
        else if (code >= 500) out_key = u"spotlight.ai.serverError";
        else out_key = u"spotlight.ai.providerError";

        if (code > 0)
        {
            char buf[32]{};
            int n = std::snprintf(buf, sizeof(buf), "%s %d", streaming ? "API" : "HTTP", static_cast<int>(code));
            const int clamped_n = (n > 0) ? std::min<int>(n, static_cast<int>(sizeof(buf) - 1)) : 0;
            for (int i = 0; i < clamped_n; ++i)
            {
                diag_buf[i] = static_cast<char16_t>(static_cast<unsigned char>(buf[i]));
            }
            out_diag = std::u16string_view(diag_buf, static_cast<size_t>(clamped_n));
        }
        else
        {
            out_diag = u"API stream error";
        }
    }

    static void ResolveStreamError(
        std::u16string_view label,
        int32_t code,
        std::u16string_view& out_key,
        char16_t (&diag_buf)[64],
        std::u16string_view& out_diag) noexcept
    {
        if (label == u"insufficient_quota")
        {
            out_key = u"spotlight.ai.billingError";
            out_diag = u"API insufficient_quota";
            return;
        }
        if (label == u"context_length_exceeded")
        {
            out_key = u"spotlight.ai.contextError";
            out_diag = u"API context_length_exceeded";
            return;
        }

        int32_t resolved_code = (code >= 100 && code <= 599) ? code : 0;
        if (resolved_code == 0)
        {
            if (label == u"overloaded_error" || label == u"UNAVAILABLE") resolved_code = 503;
            else if (label == u"rate_limit_error" || label == u"rate_limit_exceeded" || label == u"RESOURCE_EXHAUSTED") resolved_code = 429;
            else if (label == u"authentication_error" || label == u"invalid_api_key" || label == u"UNAUTHENTICATED") resolved_code = 401;
            else if (label == u"permission_error" || label == u"PERMISSION_DENIED") resolved_code = 403;
            else if (label == u"invalid_request_error" || label == u"INVALID_ARGUMENT") resolved_code = 400;
            else if (label == u"not_found_error" || label == u"model_not_found" || label == u"NOT_FOUND") resolved_code = 404;
            else if (label == u"request_too_large") resolved_code = 413;
            else if (label == u"DEADLINE_EXCEEDED") resolved_code = 504;
            else if (label == u"api_error" || label == u"INTERNAL") resolved_code = 500;
        }
        ResolveProviderError(resolved_code, true, out_key, diag_buf, out_diag);
    }

    static void ParseSseDataLine(
        std::u16string_view line,
        int32_t& out_payload_offset,
        int32_t& out_payload_len,
        bool& out_is_data,
        bool& out_is_done) noexcept
    {
        out_payload_offset = 0;
        out_payload_len = 0;
        out_is_data = false;
        out_is_done = false;

        if (!vnotch::starts_with_ordinal(line, u"data:"))
        {
            return;
        }

        out_is_data = true;
        size_t pos = 5;
        while (pos < line.size() && vnotch::is_unicode_whitespace(line[pos]))
        {
            ++pos;
        }

        out_payload_offset = static_cast<int32_t>(pos);
        out_payload_len = static_cast<int32_t>(line.size() - pos);
        std::u16string_view payload = line.substr(pos);
        out_is_done = (payload == u"[DONE]");
    }

    // Status values for ExtractJsonDelta:
    // 0 = Fallback to JsonDocument.Parse (error object, usage metadata, complex/malformed JSON, or small buffer)
    // 1 = SSE "[DONE]" sentinel
    // 2 = Fast path extracted delta & completion state
    static int32_t ExtractJsonDelta(
        std::u16string_view payload,
        std::u16string_view provider,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_delta_len,
        uint8_t& out_status,
        uint8_t& out_completed) noexcept
    {
        out_delta_len = 0;
        out_status = 0;
        out_completed = 0;

        if (payload == u"[DONE]")
        {
            out_status = 1;
            out_completed = 1;
            return VN_OK;
        }

        if (payload.empty() || payload.size() > static_cast<size_t>(INT_MAX / 4))
        {
            return VN_OK;
        }

        try
        {
            char utf8_stack[8192];
            std::string utf8_heap;
            std::string_view utf8_view;

            const auto* wide_ptr = reinterpret_cast<const wchar_t*>(payload.data());
            const int wide_len = static_cast<int>(payload.size());

            if (payload.size() <= sizeof(utf8_stack) / 3)
            {
                const int utf8_len = WideCharToMultiByte(
                    CP_UTF8, WC_ERR_INVALID_CHARS,
                    wide_ptr, wide_len,
                    utf8_stack, static_cast<int>(sizeof(utf8_stack)),
                    nullptr, nullptr);
                if (utf8_len <= 0)
                {
                    return VN_OK;
                }
                utf8_view = std::string_view(utf8_stack, static_cast<size_t>(utf8_len));
            }
            else
            {
                const int needed = WideCharToMultiByte(
                    CP_UTF8, WC_ERR_INVALID_CHARS,
                    wide_ptr, wide_len,
                    nullptr, 0,
                    nullptr, nullptr);
                if (needed <= 0)
                {
                    return VN_OK;
                }
                utf8_heap.resize(static_cast<size_t>(needed));
                const int written_utf8 = WideCharToMultiByte(
                    CP_UTF8, WC_ERR_INVALID_CHARS,
                    wide_ptr, wide_len,
                    utf8_heap.data(), needed,
                    nullptr, nullptr);
                if (written_utf8 != needed)
                {
                    return VN_OK;
                }
                utf8_view = utf8_heap;
            }

            if (utf8_view.starts_with("\xEF\xBB\xBF"))
            {
                return VN_OK;
            }

            const vnotch::json::Value root = vnotch::json::parse(utf8_view);
            if (!root.is('{'))
            {
                return VN_OK;
            }

            int32_t written = 0;
            bool completed = false;
            bool ok = false;

            if (provider == u"Gemini")
            {
                ok = ExtractGeminiRoot(root, out_delta_buf, delta_cap, written, completed);
            }
            else if (provider == u"Claude")
            {
                ok = ExtractClaudeRoot(root, out_delta_buf, delta_cap, written, completed);
            }
            else
            {
                ok = ExtractOpenAiRoot(root, out_delta_buf, delta_cap, written);
            }

            if (!ok)
            {
                out_delta_len = 0;
                return VN_OK;
            }

            out_delta_len = written;
            out_completed = completed ? 1u : 0u;
            out_status = 2;
            return VN_OK;
        }
        catch (...)
        {
            out_delta_len = 0;
            out_status = 0;
            out_completed = 0;
            return VN_OK;
        }
    }

private:
    static bool KeyEquals(const vnotch::json::Value& key, std::string_view expected)
    {
        if (key.bytes.find('\\') == std::string_view::npos)
        {
            return key.bytes.size() == expected.size() + 2 &&
                   key.bytes.substr(1, expected.size()) == expected;
        }
        const std::u16string decoded = key.text();
        if (decoded.size() != expected.size())
        {
            return false;
        }
        for (size_t i = 0; i < expected.size(); ++i)
        {
            if (decoded[i] != static_cast<char16_t>(static_cast<unsigned char>(expected[i])))
            {
                return false;
            }
        }
        return true;
    }

    static bool AppendJsonString(
        const vnotch::json::Value& val,
        char16_t* out_buf,
        int32_t cap,
        int32_t& io_written)
    {
        if (!val.is('"'))
        {
            return false;
        }
        const std::u16string decoded = val.text();
        if (decoded.empty())
        {
            return true;
        }
        if (out_buf == nullptr ||
            static_cast<int64_t>(io_written) + static_cast<int64_t>(decoded.size()) > static_cast<int64_t>(cap))
        {
            return false;
        }
        std::char_traits<char16_t>::copy(out_buf + io_written, decoded.data(), decoded.size());
        io_written += static_cast<int32_t>(decoded.size());
        return true;
    }

    static bool ExtractOpenAiRoot(
        const vnotch::json::Value& root,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written)
    {
        bool ok = true;
        root.each([&](vnotch::json::Value key, vnotch::json::Value val) {
            if (KeyEquals(key, "error"))
            {
                ok = false;
                return false;
            }
            if (KeyEquals(key, "usage"))
            {
                if (val.bytes != "null")
                {
                    ok = false;
                    return false;
                }
                return true;
            }
            if (KeyEquals(key, "choices"))
            {
                if (!val.is('['))
                {
                    ok = false;
                    return false;
                }
                int32_t idx = 0;
                val.each([&](vnotch::json::Value, vnotch::json::Value choice) {
                    if (idx++ > 0)
                    {
                        return true;
                    }
                    if (!choice.is('{'))
                    {
                        ok = false;
                        return false;
                    }
                    choice.each([&](vnotch::json::Value ckey, vnotch::json::Value cval) {
                        if (!KeyEquals(ckey, "delta") || cval.bytes == "null")
                        {
                            return true;
                        }
                        if (!cval.is('{'))
                        {
                            ok = false;
                            return false;
                        }
                        cval.each([&](vnotch::json::Value dkey, vnotch::json::Value dval) {
                            if (!KeyEquals(dkey, "content") || dval.bytes == "null")
                            {
                                return true;
                            }
                            if (!AppendJsonString(dval, out_delta_buf, delta_cap, out_written))
                            {
                                ok = false;
                                return false;
                            }
                            return true;
                        });
                        return ok;
                    });
                    return ok;
                });
                return ok;
            }
            return true;
        });
        return ok;
    }

    static bool ExtractClaudeRoot(
        const vnotch::json::Value& root,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written,
        bool& out_completed)
    {
        bool ok = true;
        root.each([&](vnotch::json::Value key, vnotch::json::Value val) {
            if (KeyEquals(key, "error") || KeyEquals(key, "usage") || KeyEquals(key, "message"))
            {
                ok = false;
                return false;
            }
            if (KeyEquals(key, "type"))
            {
                if (!val.is('"'))
                {
                    ok = false;
                    return false;
                }
                if (val.text() == u"message_stop")
                {
                    out_completed = true;
                }
                return true;
            }
            if (KeyEquals(key, "delta"))
            {
                if (!val.is('{'))
                {
                    ok = false;
                    return false;
                }
                bool is_text_delta = false;
                const int32_t delta_start = out_written;
                val.each([&](vnotch::json::Value dkey, vnotch::json::Value dval) {
                    if (KeyEquals(dkey, "type"))
                    {
                        if (!dval.is('"'))
                        {
                            ok = false;
                            return false;
                        }
                        if (dval.text() == u"text_delta")
                        {
                            is_text_delta = true;
                        }
                    }
                    else if (KeyEquals(dkey, "text"))
                    {
                        if (dval.bytes != "null" &&
                            !AppendJsonString(dval, out_delta_buf, delta_cap, out_written))
                        {
                            ok = false;
                            return false;
                        }
                    }
                    return true;
                });
                if (!ok)
                {
                    return false;
                }
                if (!is_text_delta)
                {
                    out_written = delta_start;
                }
                return true;
            }
            return true;
        });
        return ok;
    }

    static bool ExtractGeminiRoot(
        const vnotch::json::Value& root,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written,
        bool& out_completed)
    {
        bool ok = true;
        root.each([&](vnotch::json::Value key, vnotch::json::Value val) {
            if (KeyEquals(key, "error") || KeyEquals(key, "usageMetadata"))
            {
                ok = false;
                return false;
            }
            if (KeyEquals(key, "candidates"))
            {
                if (!val.is('['))
                {
                    ok = false;
                    return false;
                }
                int32_t c_idx = 0;
                val.each([&](vnotch::json::Value, vnotch::json::Value cand) {
                    if (!cand.is('{'))
                    {
                        ok = false;
                        return false;
                    }
                    const int32_t cur_idx = c_idx++;
                    cand.each([&](vnotch::json::Value ckey, vnotch::json::Value cval) {
                        if (KeyEquals(ckey, "finishReason"))
                        {
                            if (cval.bytes == "null")
                            {
                                return true;
                            }
                            if (!cval.is('"'))
                            {
                                ok = false;
                                return false;
                            }
                            if (cval.text() == u"STOP")
                            {
                                out_completed = true;
                            }
                        }
                        else if (cur_idx == 0 && KeyEquals(ckey, "content"))
                        {
                            if (!cval.is('{'))
                            {
                                ok = false;
                                return false;
                            }
                            cval.each([&](vnotch::json::Value ctkey, vnotch::json::Value ctval) {
                                if (!KeyEquals(ctkey, "parts"))
                                {
                                    return true;
                                }
                                if (!ctval.is('['))
                                {
                                    ok = false;
                                    return false;
                                }
                                ctval.each([&](vnotch::json::Value, vnotch::json::Value part) {
                                    if (!part.is('{'))
                                    {
                                        ok = false;
                                        return false;
                                    }
                                    const int32_t part_start = out_written;
                                    bool is_thought = false;
                                    part.each([&](vnotch::json::Value pkey, vnotch::json::Value pval) {
                                        if (KeyEquals(pkey, "text"))
                                        {
                                            if (!AppendJsonString(pval, out_delta_buf, delta_cap, out_written))
                                            {
                                                ok = false;
                                                return false;
                                            }
                                        }
                                        else if (KeyEquals(pkey, "thought"))
                                        {
                                            if (pval.bytes == "true")
                                            {
                                                is_thought = true;
                                            }
                                            else if (pval.bytes != "false" && pval.bytes != "null")
                                            {
                                                ok = false;
                                                return false;
                                            }
                                        }
                                        return true;
                                    });
                                    if (!ok)
                                    {
                                        return false;
                                    }
                                    if (is_thought)
                                    {
                                        out_written = part_start;
                                    }
                                    return true;
                                });
                                return ok;
                            });
                        }
                        return ok;
                    });
                    return ok;
                });
                return ok;
            }
            return true;
        });
        return ok;
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_spotlight_ai_validate_api_key(
    const char16_t* key_ptr,
    int32_t key_len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = vnotch::services::SpotlightAiService::ValidateApiKey(
        vnotch::make_u16_view(key_ptr, key_len)) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_spotlight_ai_resolve_provider_error(
    int32_t code,
    uint8_t streaming,
    char16_t* out_key_buf,
    int32_t key_cap,
    int32_t* out_key_len,
    char16_t* out_diag_buf,
    int32_t diag_cap,
    int32_t* out_diag_len) noexcept
{
    std::u16string_view key;
    char16_t diag_storage[64]{};
    std::u16string_view diag;
    vnotch::services::SpotlightAiService::ResolveProviderError(code, streaming != 0u, key, diag_storage, diag);
    int32_t rc1 = vnotch::copy_out_u16(key, out_key_buf, key_cap, out_key_len);
    if (rc1 != VN_OK) return rc1;
    return vnotch::copy_out_u16(diag, out_diag_buf, diag_cap, out_diag_len);
}

VN_EXPORT int32_t vn_spotlight_ai_resolve_stream_error(
    const char16_t* label_ptr,
    int32_t label_len,
    int32_t code,
    char16_t* out_key_buf,
    int32_t key_cap,
    int32_t* out_key_len,
    char16_t* out_diag_buf,
    int32_t diag_cap,
    int32_t* out_diag_len) noexcept
{
    std::u16string_view key;
    char16_t diag_storage[64]{};
    std::u16string_view diag;
    vnotch::services::SpotlightAiService::ResolveStreamError(
        vnotch::make_u16_view(label_ptr, label_len),
        code,
        key,
        diag_storage,
        diag);
    int32_t rc1 = vnotch::copy_out_u16(key, out_key_buf, key_cap, out_key_len);
    if (rc1 != VN_OK) return rc1;
    return vnotch::copy_out_u16(diag, out_diag_buf, diag_cap, out_diag_len);
}

extern "C" VN_EXPORT int32_t __stdcall vn_spotlight_ai_parse_sse_data_line(
    const char16_t* line_ptr,
    int32_t line_len,
    int32_t* out_payload_offset,
    int32_t* out_payload_len,
    uint8_t* out_is_data,
    uint8_t* out_is_done) noexcept
{
    if (line_len < 0 || (line_len > 0 && line_ptr == nullptr) ||
        out_payload_offset == nullptr || out_payload_len == nullptr ||
        out_is_data == nullptr || out_is_done == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    bool is_data = false;
    bool is_done = false;
    vnotch::services::SpotlightAiService::ParseSseDataLine(
        vnotch::make_u16_view(line_ptr, line_len),
        *out_payload_offset,
        *out_payload_len,
        is_data,
        is_done);
    *out_is_data = is_data ? 1u : 0u;
    *out_is_done = is_done ? 1u : 0u;
    return VN_OK;
}

extern "C" VN_EXPORT int32_t __stdcall vn_spotlight_ai_extract_json_delta(
    const char16_t* payload_ptr,
    int32_t payload_len,
    const char16_t* provider_ptr,
    int32_t provider_len,
    char16_t* out_delta_buf,
    int32_t delta_cap,
    int32_t* out_delta_len,
    uint8_t* out_status,
    uint8_t* out_completed) noexcept
{
    if (payload_len < 0 || (payload_len > 0 && payload_ptr == nullptr) ||
        provider_len < 0 || (provider_len > 0 && provider_ptr == nullptr) ||
        delta_cap < 0 || (delta_cap > 0 && out_delta_buf == nullptr) ||
        out_delta_len == nullptr || out_status == nullptr || out_completed == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }

    return vnotch::services::SpotlightAiService::ExtractJsonDelta(
        vnotch::make_u16_view(payload_ptr, payload_len),
        vnotch::make_u16_view(provider_ptr, provider_len),
        out_delta_buf,
        delta_cap,
        *out_delta_len,
        *out_status,
        *out_completed);
}
