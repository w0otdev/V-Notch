#include "vnotch_common.h"

namespace vnotch::services
{

class AiUsageSnapshot final
{
public:
    static constexpr bool ValidateCredentialKey(std::u16string_view key) noexcept
    {
        std::u16string_view trimmed = vnotch::trim_view(key);
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

    static constexpr int64_t SumClaudeTokens(
        int64_t input,
        int64_t output,
        int64_t cache_read,
        int64_t cache_created) noexcept
    {
        return input + output + cache_read + cache_created;
    }
};

} // namespace vnotch::services

extern "C"
{

VN_EXPORT int32_t vn_ai_usage_validate_credential_key(
    const char16_t* key_ptr,
    int32_t key_len,
    uint8_t* out_valid) noexcept
{
    if (out_valid == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_valid = (key_ptr != nullptr &&
                  vnotch::services::AiUsageSnapshot::ValidateCredentialKey(vnotch::make_u16_view(key_ptr, key_len))) ? 1u : 0u;
    return VN_OK;
}

VN_EXPORT int32_t vn_ai_usage_sum_claude_tokens(
    int64_t input,
    int64_t output,
    int64_t cache_read,
    int64_t cache_created,
    int64_t* out_total) noexcept
{
    if (out_total == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_total = vnotch::services::AiUsageSnapshot::SumClaudeTokens(input, output, cache_read, cache_created);
    return VN_OK;
}

} // extern "C"
