#include "../vnotch_common.h"

namespace vnotch::services {

class CoalescingActionQueue final {
public:
    static void EvaluatePostDecision(
        bool key_already_pending,
        bool is_running,
        bool* out_should_enqueue_key,
        bool* out_should_start_worker) noexcept {
        if (out_should_enqueue_key) *out_should_enqueue_key = !key_already_pending;
        if (out_should_start_worker) *out_should_start_worker = !is_running;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_coalescing_action_queue_evaluate_post(
    uint8_t key_already_pending,
    uint8_t is_running,
    uint8_t* out_should_enqueue_key,
    uint8_t* out_should_start_worker) noexcept {
    if (!out_should_enqueue_key || !out_should_start_worker) return VN_E_INVALID_ARG;
    bool enqueue_key = false;
    bool start_worker = false;
    vnotch::services::CoalescingActionQueue::EvaluatePostDecision(
        key_already_pending != 0, is_running != 0, &enqueue_key, &start_worker);
    *out_should_enqueue_key = enqueue_key ? 1u : 0u;
    *out_should_start_worker = start_worker ? 1u : 0u;
    return VN_OK;
}

} // extern "C"
