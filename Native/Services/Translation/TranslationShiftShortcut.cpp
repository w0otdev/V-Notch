#include "vnotch_common.h"
#include <cstdint>

namespace vnotch::services {

#pragma pack(push, 1)
struct VnShiftTapState {
    uint32_t held_key;
    uint32_t down_time;
    uint32_t last_tap;
    uint32_t last_shift_event;
    uint32_t last_other_event;
    intptr_t window;
    uint8_t has_tap;
    uint8_t second_tap;
    uint8_t waiting_for_quiet;
    uint8_t has_other_event;
};
#pragma pack(pop)

class TranslationShiftShortcut final {
public:
    static void Reset(VnShiftTapState& state) noexcept {
        ResetTaps(state);
        state.waiting_for_quiet = 0;
        state.has_other_event = 0;
    }

    static void ResetTaps(VnShiftTapState& state) noexcept {
        state.held_key = 0;
        state.has_tap = 0;
        state.second_tap = 0;
        state.window = 0;
    }

    static bool Process(VnShiftTapState& state, uint32_t key, bool down, uint32_t time, intptr_t window, bool blocked) noexcept {
        bool shift = (key == 0x10u || key == 0xA0u || key == 0xA1u);
        if (shift) {
            bool quiet = static_cast<uint32_t>(time - state.last_shift_event) > 500u;
            state.last_shift_event = time;
            if (state.waiting_for_quiet != 0 && !quiet) {
                ResetTaps(state);
                return false;
            }
            if (quiet) {
                state.waiting_for_quiet = 0;
            }
        }
        if (blocked || window == 0) {
            ResetTaps(state);
            return false;
        }
        if (!shift) {
            state.last_other_event = time;
            state.has_other_event = 1;
            ResetTaps(state);
            return false;
        }
        if (state.window != window) {
            ResetTaps(state);
            state.window = window;
        }
        if (down) {
            if (state.has_tap == 0 && state.has_other_event != 0 && static_cast<uint32_t>(time - state.last_other_event) < 150u) {
                ResetTaps(state);
                return false;
            }
            if (state.held_key == key) return false;
            if (state.held_key != 0) {
                ResetTaps(state);
                return false;
            }
            state.second_tap = (state.has_tap != 0 && static_cast<uint32_t>(time - state.last_tap) <= 500u) ? 1 : 0;
            state.held_key = key;
            state.down_time = time;
            return false;
        }
        if (state.held_key != key) {
            ResetTaps(state);
            return false;
        }
        state.held_key = 0;
        if (static_cast<uint32_t>(time - state.down_time) > 300u) {
            state.has_tap = 0;
            return false;
        }
        bool triggered = state.second_tap != 0;
        state.has_tap = triggered ? 0 : 1;
        state.last_tap = time;
        if (triggered) state.waiting_for_quiet = 1;
        return triggered;
    }
};

} // namespace vnotch::services

extern "C" {

VN_EXPORT int32_t vn_transshift_reset(
    vnotch::services::VnShiftTapState* state) noexcept
{
    if (!state) return VN_ERR_INVALID_ARG;
    vnotch::services::TranslationShiftShortcut::Reset(*state);
    return VN_OK;
}

VN_EXPORT int32_t vn_transshift_process(
    vnotch::services::VnShiftTapState* state,
    uint32_t key, uint8_t down, uint32_t time, intptr_t window, uint8_t blocked,
    uint8_t* out_triggered) noexcept
{
    if (!state || !out_triggered) return VN_ERR_INVALID_ARG;
    *out_triggered = vnotch::services::TranslationShiftShortcut::Process(
        *state, key, down != 0, time, window, blocked != 0) ? 1 : 0;
    return VN_OK;
}

} // extern "C"
