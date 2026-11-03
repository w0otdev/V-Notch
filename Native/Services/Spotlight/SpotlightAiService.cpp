#include "vnotch_common.h"
#include <algorithm>
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

        size_t pos = 0;
        SkipJsonWs(payload, pos);
        if (pos >= payload.size() || payload[pos] != u'{')
        {
            return VN_OK;
        }

        if (provider == u"Gemini")
        {
            bool completed = false;
            int32_t written = 0;
            if (!ParseGeminiRoot(payload, pos, out_delta_buf, delta_cap, written, completed))
            {
                out_delta_len = 0;
                return VN_OK;
            }
            SkipJsonWs(payload, pos);
            if (pos != payload.size())
            {
                out_delta_len = 0;
                return VN_OK;
            }
            out_delta_len = written;
            out_completed = completed ? 1u : 0u;
            out_status = 2;
            return VN_OK;
        }

        if (provider == u"Claude")
        {
            bool completed = false;
            int32_t written = 0;
            if (!ParseClaudeRoot(payload, pos, out_delta_buf, delta_cap, written, completed))
            {
                out_delta_len = 0;
                return VN_OK;
            }
            SkipJsonWs(payload, pos);
            if (pos != payload.size())
            {
                out_delta_len = 0;
                return VN_OK;
            }
            out_delta_len = written;
            out_completed = completed ? 1u : 0u;
            out_status = 2;
            return VN_OK;
        }

        {
            int32_t written = 0;
            if (!ParseOpenAiRoot(payload, pos, out_delta_buf, delta_cap, written))
            {
                out_delta_len = 0;
                return VN_OK;
            }
            SkipJsonWs(payload, pos);
            if (pos != payload.size())
            {
                out_delta_len = 0;
                return VN_OK;
            }
            out_delta_len = written;
            out_completed = 0u;
            out_status = 2;
            return VN_OK;
        }
    }

private:
    static constexpr void SkipJsonWs(std::u16string_view sv, size_t& pos) noexcept
    {
        while (pos < sv.size())
        {
            char16_t ch = sv[pos];
            if (ch == u' ' || ch == u'\t' || ch == u'\r' || ch == u'\n')
            {
                ++pos;
            }
            else
            {
                break;
            }
        }
    }

    static constexpr int32_t HexVal(char16_t ch) noexcept
    {
        if (ch >= u'0' && ch <= u'9') return ch - u'0';
        if (ch >= u'a' && ch <= u'f') return 10 + (ch - u'a');
        if (ch >= u'A' && ch <= u'F') return 10 + (ch - u'A');
        return -1;
    }

    static constexpr bool ParseHex4(std::u16string_view sv, size_t& pos, uint16_t& out_val) noexcept
    {
        if (pos + 4 > sv.size()) return false;
        uint32_t acc = 0;
        for (int i = 0; i < 4; ++i)
        {
            int32_t hv = HexVal(sv[pos + static_cast<size_t>(i)]);
            if (hv < 0) return false;
            acc = (acc << 4) | static_cast<uint32_t>(hv);
        }
        pos += 4;
        out_val = static_cast<uint16_t>(acc);
        return true;
    }

    static constexpr bool ParseJsonString(
        std::u16string_view sv,
        size_t& pos,
        char16_t* out_buf,
        int32_t cap,
        int32_t& io_len) noexcept
    {
        if (pos >= sv.size() || sv[pos] != u'"') return false;
        ++pos;

        while (pos < sv.size())
        {
            char16_t ch = sv[pos++];
            if (ch == u'"')
            {
                return true;
            }
            if (ch < 0x20u)
            {
                return false;
            }
            if (ch == u'\\')
            {
                if (pos >= sv.size()) return false;
                char16_t esc = sv[pos++];
                char16_t decoded = 0;
                switch (esc)
                {
                case u'"': decoded = u'"'; break;
                case u'\\': decoded = u'\\'; break;
                case u'/': decoded = u'/'; break;
                case u'b': decoded = u'\b'; break;
                case u'f': decoded = u'\f'; break;
                case u'n': decoded = u'\n'; break;
                case u'r': decoded = u'\r'; break;
                case u't': decoded = u'\t'; break;
                case u'u':
                {
                    uint16_t u1 = 0;
                    if (!ParseHex4(sv, pos, u1)) return false;
                    if (u1 >= 0xD800u && u1 <= 0xDBFFu)
                    {
                        if (pos + 2 > sv.size() || sv[pos] != u'\\' || sv[pos + 1] != u'u')
                            return false;
                        pos += 2;
                        uint16_t u2 = 0;
                        if (!ParseHex4(sv, pos, u2) || u2 < 0xDC00u || u2 > 0xDFFFu)
                            return false;
                        if (out_buf != nullptr)
                        {
                            if (io_len + 2 > cap) return false;
                            out_buf[io_len++] = static_cast<char16_t>(u1);
                            out_buf[io_len++] = static_cast<char16_t>(u2);
                        }
                        continue;
                    }
                    if (u1 >= 0xDC00u && u1 <= 0xDFFFu)
                    {
                        return false;
                    }
                    decoded = static_cast<char16_t>(u1);
                    break;
                }
                default:
                    return false;
                }
                if (out_buf != nullptr)
                {
                    if (io_len >= cap) return false;
                    out_buf[io_len++] = decoded;
                }
            }
            else
            {
                if (out_buf != nullptr)
                {
                    if (io_len >= cap) return false;
                    out_buf[io_len++] = ch;
                }
            }
        }
        return false;
    }

    static constexpr bool SkipJsonNumber(std::u16string_view sv, size_t& pos) noexcept
    {
        if (pos < sv.size() && sv[pos] == u'-') ++pos;
        if (pos >= sv.size()) return false;
        if (sv[pos] == u'0')
        {
            ++pos;
        }
        else if (sv[pos] >= u'1' && sv[pos] <= u'9')
        {
            while (pos < sv.size() && sv[pos] >= u'0' && sv[pos] <= u'9') ++pos;
        }
        else
        {
            return false;
        }
        if (pos < sv.size() && sv[pos] == u'.')
        {
            ++pos;
            if (pos >= sv.size() || sv[pos] < u'0' || sv[pos] > u'9') return false;
            while (pos < sv.size() && sv[pos] >= u'0' && sv[pos] <= u'9') ++pos;
        }
        if (pos < sv.size() && (sv[pos] == u'e' || sv[pos] == u'E'))
        {
            ++pos;
            if (pos < sv.size() && (sv[pos] == u'+' || sv[pos] == u'-')) ++pos;
            if (pos >= sv.size() || sv[pos] < u'0' || sv[pos] > u'9') return false;
            while (pos < sv.size() && sv[pos] >= u'0' && sv[pos] <= u'9') ++pos;
        }
        return true;
    }

    static constexpr bool MatchLiteral(std::u16string_view sv, size_t& pos, std::u16string_view lit) noexcept
    {
        if (pos + lit.size() > sv.size()) return false;
        if (sv.substr(pos, lit.size()) != lit) return false;
        pos += lit.size();
        return true;
    }

    static bool SkipJsonValue(std::u16string_view sv, size_t& pos, int depth = 0) noexcept
    {
        if (depth > 24) return false;
        SkipJsonWs(sv, pos);
        if (pos >= sv.size()) return false;

        char16_t ch = sv[pos];
        if (ch == u'"')
        {
            int32_t dummy = 0;
            return ParseJsonString(sv, pos, nullptr, 0, dummy);
        }
        if (ch == u'{')
        {
            ++pos;
            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u'}')
            {
                ++pos;
                return true;
            }
            while (pos < sv.size())
            {
                SkipJsonWs(sv, pos);
                int32_t dummy = 0;
                if (!ParseJsonString(sv, pos, nullptr, 0, dummy)) return false;
                SkipJsonWs(sv, pos);
                if (pos >= sv.size() || sv[pos] != u':') return false;
                ++pos;
                if (!SkipJsonValue(sv, pos, depth + 1)) return false;
                SkipJsonWs(sv, pos);
                if (pos < sv.size() && sv[pos] == u',')
                {
                    ++pos;
                    continue;
                }
                if (pos < sv.size() && sv[pos] == u'}')
                {
                    ++pos;
                    return true;
                }
                return false;
            }
            return false;
        }
        if (ch == u'[')
        {
            ++pos;
            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u']')
            {
                ++pos;
                return true;
            }
            while (pos < sv.size())
            {
                if (!SkipJsonValue(sv, pos, depth + 1)) return false;
                SkipJsonWs(sv, pos);
                if (pos < sv.size() && sv[pos] == u',')
                {
                    ++pos;
                    continue;
                }
                if (pos < sv.size() && sv[pos] == u']')
                {
                    ++pos;
                    return true;
                }
                return false;
            }
            return false;
        }
        if (ch == u't') return MatchLiteral(sv, pos, u"true");
        if (ch == u'f') return MatchLiteral(sv, pos, u"false");
        if (ch == u'n') return MatchLiteral(sv, pos, u"null");
        if (ch == u'-' || (ch >= u'0' && ch <= u'9')) return SkipJsonNumber(sv, pos);
        return false;
    }

    static bool ParseOpenAiDeltaObject(
        std::u16string_view sv,
        size_t& pos,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written) noexcept
    {
        SkipJsonWs(sv, pos);
        if (MatchLiteral(sv, pos, u"null")) return true;
        if (pos >= sv.size() || sv[pos] != u'{') return false;
        ++pos;
        SkipJsonWs(sv, pos);
        if (pos < sv.size() && sv[pos] == u'}')
        {
            ++pos;
            return true;
        }
        while (pos < sv.size())
        {
            SkipJsonWs(sv, pos);
            char16_t key_buf[64]{};
            int32_t key_len = 0;
            if (!ParseJsonString(sv, pos, key_buf, 64, key_len)) return false;
            std::u16string_view key(key_buf, static_cast<size_t>(key_len));
            SkipJsonWs(sv, pos);
            if (pos >= sv.size() || sv[pos] != u':') return false;
            ++pos;
            SkipJsonWs(sv, pos);

            if (key == u"content")
            {
                if (MatchLiteral(sv, pos, u"null"))
                {
                    // null content -> empty delta
                }
                else if (pos < sv.size() && sv[pos] == u'"')
                {
                    if (!ParseJsonString(sv, pos, out_delta_buf, delta_cap, out_written))
                        return false;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                if (!SkipJsonValue(sv, pos, 2)) return false;
            }

            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u',')
            {
                ++pos;
                continue;
            }
            if (pos < sv.size() && sv[pos] == u'}')
            {
                ++pos;
                return true;
            }
            return false;
        }
        return false;
    }

    static bool ParseOpenAiChoicesArray(
        std::u16string_view sv,
        size_t& pos,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written) noexcept
    {
        SkipJsonWs(sv, pos);
        if (pos >= sv.size() || sv[pos] != u'[') return false;
        ++pos;
        SkipJsonWs(sv, pos);
        if (pos < sv.size() && sv[pos] == u']')
        {
            ++pos;
            return true;
        }

        int32_t idx = 0;
        while (pos < sv.size())
        {
            SkipJsonWs(sv, pos);
            if (idx == 0)
            {
                if (pos >= sv.size() || sv[pos] != u'{') return false;
                ++pos;
                SkipJsonWs(sv, pos);
                if (pos < sv.size() && sv[pos] == u'}')
                {
                    ++pos;
                }
                else
                {
                    while (pos < sv.size())
                    {
                        SkipJsonWs(sv, pos);
                        char16_t key_buf[64]{};
                        int32_t key_len = 0;
                        if (!ParseJsonString(sv, pos, key_buf, 64, key_len)) return false;
                        std::u16string_view key(key_buf, static_cast<size_t>(key_len));
                        SkipJsonWs(sv, pos);
                        if (pos >= sv.size() || sv[pos] != u':') return false;
                        ++pos;
                        if (key == u"delta")
                        {
                            if (!ParseOpenAiDeltaObject(sv, pos, out_delta_buf, delta_cap, out_written))
                                return false;
                        }
                        else
                        {
                            if (!SkipJsonValue(sv, pos, 2)) return false;
                        }
                        SkipJsonWs(sv, pos);
                        if (pos < sv.size() && sv[pos] == u',')
                        {
                            ++pos;
                            continue;
                        }
                        if (pos < sv.size() && sv[pos] == u'}')
                        {
                            ++pos;
                            break;
                        }
                        return false;
                    }
                }
            }
            else
            {
                if (!SkipJsonValue(sv, pos, 1)) return false;
            }
            ++idx;
            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u',')
            {
                ++pos;
                continue;
            }
            if (pos < sv.size() && sv[pos] == u']')
            {
                ++pos;
                return true;
            }
            return false;
        }
        return false;
    }

    static bool ParseOpenAiRoot(
        std::u16string_view sv,
        size_t& pos,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written) noexcept
    {
        if (pos >= sv.size() || sv[pos] != u'{') return false;
        ++pos;
        SkipJsonWs(sv, pos);
        if (pos < sv.size() && sv[pos] == u'}')
        {
            ++pos;
            return true;
        }
        while (pos < sv.size())
        {
            SkipJsonWs(sv, pos);
            char16_t key_buf[64]{};
            int32_t key_len = 0;
            if (!ParseJsonString(sv, pos, key_buf, 64, key_len)) return false;
            std::u16string_view key(key_buf, static_cast<size_t>(key_len));
            SkipJsonWs(sv, pos);
            if (pos >= sv.size() || sv[pos] != u':') return false;
            ++pos;
            SkipJsonWs(sv, pos);

            if (key == u"error")
            {
                return false;
            }
            if (key == u"usage")
            {
                if (!MatchLiteral(sv, pos, u"null"))
                    return false;
            }
            else if (key == u"choices")
            {
                if (!ParseOpenAiChoicesArray(sv, pos, out_delta_buf, delta_cap, out_written))
                    return false;
            }
            else
            {
                if (!SkipJsonValue(sv, pos, 1)) return false;
            }

            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u',')
            {
                ++pos;
                continue;
            }
            if (pos < sv.size() && sv[pos] == u'}')
            {
                ++pos;
                return true;
            }
            return false;
        }
        return false;
    }

    static bool ParseClaudeRoot(
        std::u16string_view sv,
        size_t& pos,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written,
        bool& out_completed) noexcept
    {
        if (pos >= sv.size() || sv[pos] != u'{') return false;
        ++pos;
        SkipJsonWs(sv, pos);
        if (pos < sv.size() && sv[pos] == u'}')
        {
            ++pos;
            return true;
        }
        while (pos < sv.size())
        {
            SkipJsonWs(sv, pos);
            char16_t key_buf[64]{};
            int32_t key_len = 0;
            if (!ParseJsonString(sv, pos, key_buf, 64, key_len)) return false;
            std::u16string_view key(key_buf, static_cast<size_t>(key_len));
            SkipJsonWs(sv, pos);
            if (pos >= sv.size() || sv[pos] != u':') return false;
            ++pos;
            SkipJsonWs(sv, pos);

            if (key == u"error" || key == u"usage" || key == u"message")
            {
                return false;
            }
            if (key == u"type")
            {
                char16_t type_buf[64]{};
                int32_t type_len = 0;
                if (!ParseJsonString(sv, pos, type_buf, 64, type_len)) return false;
                if (std::u16string_view(type_buf, static_cast<size_t>(type_len)) == u"message_stop")
                {
                    out_completed = true;
                }
            }
            else if (key == u"delta")
            {
                if (pos >= sv.size() || sv[pos] != u'{') return false;
                ++pos;
                SkipJsonWs(sv, pos);
                bool is_text_delta = false;
                int32_t delta_start = out_written;
                if (pos < sv.size() && sv[pos] == u'}')
                {
                    ++pos;
                }
                else
                {
                    while (pos < sv.size())
                    {
                        SkipJsonWs(sv, pos);
                        char16_t dkey_buf[64]{};
                        int32_t dkey_len = 0;
                        if (!ParseJsonString(sv, pos, dkey_buf, 64, dkey_len)) return false;
                        std::u16string_view dkey(dkey_buf, static_cast<size_t>(dkey_len));
                        SkipJsonWs(sv, pos);
                        if (pos >= sv.size() || sv[pos] != u':') return false;
                        ++pos;
                        SkipJsonWs(sv, pos);

                        if (dkey == u"type")
                        {
                            char16_t dtype_buf[64]{};
                            int32_t dtype_len = 0;
                            if (!ParseJsonString(sv, pos, dtype_buf, 64, dtype_len)) return false;
                            if (std::u16string_view(dtype_buf, static_cast<size_t>(dtype_len)) == u"text_delta")
                            {
                                is_text_delta = true;
                            }
                        }
                        else if (dkey == u"text")
                        {
                            if (MatchLiteral(sv, pos, u"null"))
                            {
                            }
                            else if (!ParseJsonString(sv, pos, out_delta_buf, delta_cap, out_written))
                            {
                                return false;
                            }
                        }
                        else
                        {
                            if (!SkipJsonValue(sv, pos, 2)) return false;
                        }

                        SkipJsonWs(sv, pos);
                        if (pos < sv.size() && sv[pos] == u',')
                        {
                            ++pos;
                            continue;
                        }
                        if (pos < sv.size() && sv[pos] == u'}')
                        {
                            ++pos;
                            break;
                        }
                        return false;
                    }
                }
                if (!is_text_delta)
                {
                    out_written = delta_start;
                }
            }
            else
            {
                if (!SkipJsonValue(sv, pos, 1)) return false;
            }

            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u',')
            {
                ++pos;
                continue;
            }
            if (pos < sv.size() && sv[pos] == u'}')
            {
                ++pos;
                return true;
            }
            return false;
        }
        return false;
    }

    static bool ParseGeminiPartsArray(
        std::u16string_view sv,
        size_t& pos,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written) noexcept
    {
        SkipJsonWs(sv, pos);
        if (pos >= sv.size() || sv[pos] != u'[') return false;
        ++pos;
        SkipJsonWs(sv, pos);
        if (pos < sv.size() && sv[pos] == u']')
        {
            ++pos;
            return true;
        }
        while (pos < sv.size())
        {
            SkipJsonWs(sv, pos);
            if (pos >= sv.size() || sv[pos] != u'{') return false;
            ++pos;
            SkipJsonWs(sv, pos);
            int32_t part_start = out_written;
            bool is_thought = false;
            if (pos < sv.size() && sv[pos] == u'}')
            {
                ++pos;
            }
            else
            {
                while (pos < sv.size())
                {
                    SkipJsonWs(sv, pos);
                    char16_t pkey_buf[64]{};
                    int32_t pkey_len = 0;
                    if (!ParseJsonString(sv, pos, pkey_buf, 64, pkey_len)) return false;
                    std::u16string_view pkey(pkey_buf, static_cast<size_t>(pkey_len));
                    SkipJsonWs(sv, pos);
                    if (pos >= sv.size() || sv[pos] != u':') return false;
                    ++pos;
                    SkipJsonWs(sv, pos);

                    if (pkey == u"text")
                    {
                        if (!ParseJsonString(sv, pos, out_delta_buf, delta_cap, out_written))
                            return false;
                    }
                    else if (pkey == u"thought")
                    {
                        if (MatchLiteral(sv, pos, u"true"))
                        {
                            is_thought = true;
                        }
                        else if (!MatchLiteral(sv, pos, u"false") && !MatchLiteral(sv, pos, u"null"))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (!SkipJsonValue(sv, pos, 4)) return false;
                    }

                    SkipJsonWs(sv, pos);
                    if (pos < sv.size() && sv[pos] == u',')
                    {
                        ++pos;
                        continue;
                    }
                    if (pos < sv.size() && sv[pos] == u'}')
                    {
                        ++pos;
                        break;
                    }
                    return false;
                }
            }
            if (is_thought)
            {
                out_written = part_start;
            }

            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u',')
            {
                ++pos;
                continue;
            }
            if (pos < sv.size() && sv[pos] == u']')
            {
                ++pos;
                return true;
            }
            return false;
        }
        return false;
    }

    static bool ParseGeminiRoot(
        std::u16string_view sv,
        size_t& pos,
        char16_t* out_delta_buf,
        int32_t delta_cap,
        int32_t& out_written,
        bool& out_completed) noexcept
    {
        if (pos >= sv.size() || sv[pos] != u'{') return false;
        ++pos;
        SkipJsonWs(sv, pos);
        if (pos < sv.size() && sv[pos] == u'}')
        {
            ++pos;
            return true;
        }
        while (pos < sv.size())
        {
            SkipJsonWs(sv, pos);
            char16_t key_buf[64]{};
            int32_t key_len = 0;
            if (!ParseJsonString(sv, pos, key_buf, 64, key_len)) return false;
            std::u16string_view key(key_buf, static_cast<size_t>(key_len));
            SkipJsonWs(sv, pos);
            if (pos >= sv.size() || sv[pos] != u':') return false;
            ++pos;
            SkipJsonWs(sv, pos);

            if (key == u"error" || key == u"usageMetadata")
            {
                return false;
            }
            if (key == u"candidates")
            {
                if (pos >= sv.size() || sv[pos] != u'[') return false;
                ++pos;
                SkipJsonWs(sv, pos);
                if (pos < sv.size() && sv[pos] == u']')
                {
                    ++pos;
                }
                else
                {
                    int32_t c_idx = 0;
                    while (pos < sv.size())
                    {
                        SkipJsonWs(sv, pos);
                        if (pos >= sv.size() || sv[pos] != u'{') return false;
                        ++pos;
                        SkipJsonWs(sv, pos);
                        if (pos < sv.size() && sv[pos] == u'}')
                        {
                            ++pos;
                        }
                        else
                        {
                            while (pos < sv.size())
                            {
                                SkipJsonWs(sv, pos);
                                char16_t ckey_buf[64]{};
                                int32_t ckey_len = 0;
                                if (!ParseJsonString(sv, pos, ckey_buf, 64, ckey_len)) return false;
                                std::u16string_view ckey(ckey_buf, static_cast<size_t>(ckey_len));
                                SkipJsonWs(sv, pos);
                                if (pos >= sv.size() || sv[pos] != u':') return false;
                                ++pos;
                                SkipJsonWs(sv, pos);

                                if (ckey == u"finishReason")
                                {
                                    char16_t r_buf[64]{};
                                    int32_t r_len = 0;
                                    if (MatchLiteral(sv, pos, u"null"))
                                    {
                                    }
                                    else if (ParseJsonString(sv, pos, r_buf, 64, r_len))
                                    {
                                        if (std::u16string_view(r_buf, static_cast<size_t>(r_len)) == u"STOP")
                                        {
                                            out_completed = true;
                                        }
                                    }
                                    else
                                    {
                                        return false;
                                    }
                                }
                                else if (ckey == u"content" && c_idx == 0)
                                {
                                    if (pos >= sv.size() || sv[pos] != u'{') return false;
                                    ++pos;
                                    SkipJsonWs(sv, pos);
                                    if (pos < sv.size() && sv[pos] == u'}')
                                    {
                                        ++pos;
                                    }
                                    else
                                    {
                                        while (pos < sv.size())
                                        {
                                            SkipJsonWs(sv, pos);
                                            char16_t ctkey_buf[64]{};
                                            int32_t ctkey_len = 0;
                                            if (!ParseJsonString(sv, pos, ctkey_buf, 64, ctkey_len)) return false;
                                            std::u16string_view ctkey(ctkey_buf, static_cast<size_t>(ctkey_len));
                                            SkipJsonWs(sv, pos);
                                            if (pos >= sv.size() || sv[pos] != u':') return false;
                                            ++pos;
                                            SkipJsonWs(sv, pos);

                                            if (ctkey == u"parts")
                                            {
                                                if (!ParseGeminiPartsArray(sv, pos, out_delta_buf, delta_cap, out_written))
                                                    return false;
                                            }
                                            else
                                            {
                                                if (!SkipJsonValue(sv, pos, 3)) return false;
                                            }

                                            SkipJsonWs(sv, pos);
                                            if (pos < sv.size() && sv[pos] == u',')
                                            {
                                                ++pos;
                                                continue;
                                            }
                                            if (pos < sv.size() && sv[pos] == u'}')
                                            {
                                                ++pos;
                                                break;
                                            }
                                            return false;
                                        }
                                    }
                                }
                                else
                                {
                                    if (!SkipJsonValue(sv, pos, 2)) return false;
                                }

                                SkipJsonWs(sv, pos);
                                if (pos < sv.size() && sv[pos] == u',')
                                {
                                    ++pos;
                                    continue;
                                }
                                if (pos < sv.size() && sv[pos] == u'}')
                                {
                                    ++pos;
                                    break;
                                }
                                return false;
                            }
                        }
                        ++c_idx;
                        SkipJsonWs(sv, pos);
                        if (pos < sv.size() && sv[pos] == u',')
                        {
                            ++pos;
                            continue;
                        }
                        if (pos < sv.size() && sv[pos] == u']')
                        {
                            ++pos;
                            break;
                        }
                        return false;
                    }
                }
            }
            else
            {
                if (!SkipJsonValue(sv, pos, 1)) return false;
            }

            SkipJsonWs(sv, pos);
            if (pos < sv.size() && sv[pos] == u',')
            {
                ++pos;
                continue;
            }
            if (pos < sv.size() && sv[pos] == u'}')
            {
                ++pos;
                return true;
            }
            return false;
        }
        return false;
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
