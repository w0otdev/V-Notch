#include "vnotch_common.h"
#include <algorithm>
#include <cstdint>
#include <string_view>

namespace vnotch::services::translation {

class QwenTranslationEngine final {
public:
    QwenTranslationEngine() = delete;

    [[nodiscard]] static constexpr bool RequiresNewContext(
        int32_t before_threads,
        int32_t after_threads,
        int32_t before_batch_threads,
        int32_t after_batch_threads,
        int32_t before_context_size,
        int32_t after_context_size,
        int32_t before_batch_size,
        int32_t after_batch_size) noexcept {
        return before_threads != after_threads ||
               before_batch_threads != after_batch_threads ||
               before_context_size != after_context_size ||
               before_batch_size != after_batch_size;
    }

    [[nodiscard]] static constexpr bool ComputeOutputBudget(
        int32_t context_size,
        int32_t used_tokens,
        int32_t reserve_tokens,
        int32_t input_tokens,
        int32_t max_output_tokens,
        int32_t& out_budget) noexcept {
        out_budget = 0;
        if (context_size <= 0 || used_tokens < 0 || reserve_tokens < 0 || input_tokens < 0 || max_output_tokens <= 0) {
            return false;
        }
        const int64_t remaining = static_cast<int64_t>(context_size) - static_cast<int64_t>(used_tokens) - static_cast<int64_t>(reserve_tokens);
        if (remaining < 16) return false;
        const int64_t desired = static_cast<int64_t>(input_tokens) * 4LL + 96LL;
        const int64_t min_budget = std::min<int64_t>(max_output_tokens, 128LL);
        const int64_t clamped_desired = std::clamp<int64_t>(desired, min_budget, static_cast<int64_t>(max_output_tokens));
        out_budget = static_cast<int32_t>(std::min<int64_t>(clamped_desired, remaining));
        return true;
    }

    [[nodiscard]] static constexpr int32_t ComputeTrimStopSequenceEnd(std::u16string_view output) noexcept {
        constexpr std::u16string_view kStops[] = {
            u"<end_of_turn>",
            u"<|endoftext|>",
            u"<|extra_1|>"
        };
        int32_t end = static_cast<int32_t>(output.size());
        const int32_t out_len = static_cast<int32_t>(output.size());

        for (const auto stop : kStops) {
            const size_t pos = output.find(stop);
            if (pos != std::u16string_view::npos) {
                end = std::min(end, static_cast<int32_t>(pos));
            }
            const int32_t stop_len = static_cast<int32_t>(stop.size());
            for (int32_t len = std::min(stop_len - 1, out_len); len > 0; --len) {
                const std::u16string_view suffix = output.substr(static_cast<size_t>(out_len - len), static_cast<size_t>(len));
                if (suffix == stop.substr(0, static_cast<size_t>(len))) {
                    end = std::min(end, out_len - len);
                    break;
                }
            }
        }
        return end;
    }
};

} // namespace vnotch::services::translation

extern "C" {

VN_EXPORT int32_t vn_qwen_engine_requires_new_context(
    int32_t before_threads,
    int32_t after_threads,
    int32_t before_batch_threads,
    int32_t after_batch_threads,
    int32_t before_context_size,
    int32_t after_context_size,
    int32_t before_batch_size,
    int32_t after_batch_size,
    uint8_t* out_requires_new) noexcept {
    if (!out_requires_new) return VN_E_INVALID_ARG;
    *out_requires_new = vnotch::services::translation::QwenTranslationEngine::RequiresNewContext(
        before_threads, after_threads, before_batch_threads, after_batch_threads,
        before_context_size, after_context_size, before_batch_size, after_batch_size) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_qwen_engine_compute_output_budget(
    int32_t context_size,
    int32_t used_tokens,
    int32_t reserve_tokens,
    int32_t input_tokens,
    int32_t max_output_tokens,
    int32_t* out_budget,
    uint8_t* out_ok) noexcept {
    if (!out_budget || !out_ok) return VN_E_INVALID_ARG;
    int32_t budget = 0;
    const bool ok = vnotch::services::translation::QwenTranslationEngine::ComputeOutputBudget(
        context_size, used_tokens, reserve_tokens, input_tokens, max_output_tokens, budget);
    *out_budget = budget;
    *out_ok = ok ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t vn_qwen_engine_compute_trim_stop_sequence_end(
    const char16_t* output,
    int32_t output_len,
    int32_t* out_end) noexcept {
    if (!out_end || (!output && output_len > 0) || output_len < 0) return VN_E_INVALID_ARG;
    const std::u16string_view sv(output ? output : u"", static_cast<size_t>(output_len));
    *out_end = vnotch::services::translation::QwenTranslationEngine::ComputeTrimStopSequenceEnd(sv);
    return VN_OK;
}

} // extern "C"
