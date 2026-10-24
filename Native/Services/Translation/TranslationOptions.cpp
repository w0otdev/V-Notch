#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnTranslationOptions {
    int32_t model_idle_minutes;
    int32_t cache_minutes;
    int32_t cache_entries;
    int32_t gpu_layers;
    int32_t threads;
    int32_t batch_threads;
    int32_t context_size;
    int32_t batch_size;
    int32_t output_tokens;
    int32_t temperature_percent;
    int32_t top_p_percent;
    int32_t top_k;
    int32_t timeout_seconds;
    uint8_t enable_humanizer;
};
#pragma pack(pop)

class TranslationOptions final {
public:
    static constexpr bool CanReuseCachedResults(const VnTranslationOptions& current, const VnTranslationOptions& previous) noexcept {
        return current.cache_minutes == previous.cache_minutes &&
               current.cache_entries == previous.cache_entries &&
               current.context_size == previous.context_size &&
               current.output_tokens == previous.output_tokens &&
               current.temperature_percent == previous.temperature_percent &&
               current.top_p_percent == previous.top_p_percent &&
               current.top_k == previous.top_k &&
               (current.enable_humanizer != 0) == (previous.enable_humanizer != 0);
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_transoptions_can_reuse_cached(
    const vnotch::services::VnTranslationOptions* current,
    const vnotch::services::VnTranslationOptions* previous,
    uint8_t* out_can_reuse) noexcept
{
    if (!current || !previous || !out_can_reuse) return VN_ERR_INVALID_ARG;
    *out_can_reuse = vnotch::services::TranslationOptions::CanReuseCachedResults(*current, *previous) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
