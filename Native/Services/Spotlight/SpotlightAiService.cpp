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
