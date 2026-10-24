#include "../vnotch_common.h"

namespace vnotch::controllers
{

class CompactPillArbiter final
{
public:
    static constexpr int32_t TryAcquire(
        int32_t requested_slot,
        int32_t* io_active_slot,
        int32_t* io_active_token,
        int32_t* io_next_token,
        int64_t* io_revision,
        uint8_t* out_won,
        int32_t* out_preempted,
        int32_t* out_token) noexcept
    {
        if (io_active_slot == nullptr || io_active_token == nullptr || io_next_token == nullptr ||
            io_revision == nullptr || out_won == nullptr || out_preempted == nullptr || out_token == nullptr)
        {
            return VN_ERR_INVALID_ARG;
        }

        if (requested_slot == 0)
        {
            *out_won = 0u;
            *out_preempted = 0;
            *out_token = 0;
            return VN_OK;
        }

        int32_t active = *io_active_slot;
        if (active != 0 && requested_slot < active)
        {
            *out_won = 0u;
            *out_preempted = active;
            *out_token = 0;
            return VN_OK;
        }

        int32_t preempted = (active != 0 && active != requested_slot) ? active : 0;
        *io_active_slot = requested_slot;
        int32_t tok = (*io_next_token)++;
        *io_active_token = tok;
        ++(*io_revision);

        *out_won = 1u;
        *out_preempted = preempted;
        *out_token = tok;
        return VN_OK;
    }
};

} // namespace vnotch::controllers

extern "C"
{

VN_EXPORT int32_t vn_compact_pill_try_acquire(
    int32_t requested_slot,
    int32_t* io_active_slot,
    int32_t* io_active_token,
    int32_t* io_next_token,
    int64_t* io_revision,
    uint8_t* out_won,
    int32_t* out_preempted,
    int32_t* out_token) noexcept
{
    return vnotch::controllers::CompactPillArbiter::TryAcquire(
        requested_slot, io_active_slot, io_active_token, io_next_token,
        io_revision, out_won, out_preempted, out_token);
}

} // extern "C"
