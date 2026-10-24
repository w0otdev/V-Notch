#include "../vnotch_common.h"
#include <cstdint>
#include <string_view>

// Module lifecycle state machine and the weather module's settings-change policy.
// The managed shell keeps the timer, events and service calls; every transition decision lives here.

namespace vnotch::modules {

#pragma pack(push, 1)
struct VnModuleLifecycle {
    uint8_t initialized;
    uint8_t running;
    uint8_t disposed;
};

struct VnWeatherSettingsInput {
    uint8_t previous_enable_weather;
    uint8_t previous_local_only;
    uint8_t previous_allow_online;
    uint8_t enable_weather;
    uint8_t local_only;
    uint8_t allow_online;
    uint8_t is_running;
    uint8_t is_fetching;
};
#pragma pack(pop)

enum WeatherAction : int32_t {
    kWeatherNone = 0,
    kWeatherDisable = 1, // stop, cancel the pending request, clear data when should_clear
    kWeatherStart = 2,   // Start() performs the first tick
    kWeatherRefresh = 3, // cancel the pending request and refresh now
};

constexpr bool WeatherActive(uint8_t enable_weather, uint8_t local_only, uint8_t allow_online) noexcept {
    return enable_weather != 0 && local_only == 0 && allow_online != 0;
}

bool CitiesEqual(std::u16string_view a, std::u16string_view b) noexcept {
    a = trim_view(a);
    b = trim_view(b);
    if (a.size() != b.size()) return false;
    if (a.empty()) return true;
    return CompareStringOrdinal(reinterpret_cast<const wchar_t*>(a.data()), static_cast<int>(a.size()),
                                reinterpret_cast<const wchar_t*>(b.data()), static_cast<int>(b.size()), TRUE) == CSTR_EQUAL;
}

} // namespace vnotch::modules

namespace mod = vnotch::modules;

extern "C" {

VN_EXPORT int32_t __stdcall vn_module_begin_initialize(const mod::VnModuleLifecycle* state, uint8_t* out_proceed) noexcept {
    if (!state || !out_proceed) return VN_ERR_INVALID_ARG;
    *out_proceed = (state->initialized == 0 && state->disposed == 0) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_module_complete_initialize(mod::VnModuleLifecycle* state) noexcept {
    if (!state) return VN_ERR_INVALID_ARG;
    state->initialized = 1;
    return VN_OK;
}

// Non-mutating plan for Start(): initialize first when needed, then commit. A failed initialize does not block Start.
VN_EXPORT int32_t __stdcall vn_module_plan_start(
    const mod::VnModuleLifecycle* state, uint8_t* out_needs_initialize, uint8_t* out_proceed) noexcept {
    if (!state || !out_needs_initialize || !out_proceed) return VN_ERR_INVALID_ARG;
    *out_needs_initialize = 0;
    *out_proceed = 0;
    if (state->disposed != 0) return VN_OK;
    *out_needs_initialize = state->initialized == 0 ? 1 : 0;
    *out_proceed = state->running == 0 ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_module_commit_start(mod::VnModuleLifecycle* state) noexcept {
    if (!state) return VN_ERR_INVALID_ARG;
    state->running = 1;
    return VN_OK;
}
VN_EXPORT int32_t __stdcall vn_module_fail_start(mod::VnModuleLifecycle* state) noexcept {
    if (!state) return VN_ERR_INVALID_ARG;
    state->running = 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_module_begin_stop(const mod::VnModuleLifecycle* state, uint8_t* out_proceed) noexcept {
    if (!state || !out_proceed) return VN_ERR_INVALID_ARG;
    *out_proceed = state->running != 0 ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_module_complete_stop(mod::VnModuleLifecycle* state) noexcept {
    if (!state) return VN_ERR_INVALID_ARG;
    state->running = 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_module_can_tick(const mod::VnModuleLifecycle* state, uint8_t* out_can_tick) noexcept {
    if (!state || !out_can_tick) return VN_ERR_INVALID_ARG;
    *out_can_tick = (state->running != 0 && state->disposed == 0) ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_module_begin_dispose(const mod::VnModuleLifecycle* state, uint8_t* out_proceed) noexcept {
    if (!state || !out_proceed) return VN_ERR_INVALID_ARG;
    *out_proceed = state->disposed == 0 ? 1 : 0;
    return VN_OK;
}

VN_EXPORT int32_t __stdcall vn_module_complete_dispose(mod::VnModuleLifecycle* state) noexcept {
    if (!state) return VN_ERR_INVALID_ARG;
    state->disposed = 1;
    return VN_OK;
}

// Whether a refresh may start for the current settings.
VN_EXPORT int32_t __stdcall vn_weather_can_refresh(
    uint8_t enable_weather, uint8_t local_only, uint8_t allow_online, uint8_t is_fetching, uint8_t* out_can_refresh) noexcept {
    if (!out_can_refresh) return VN_ERR_INVALID_ARG;
    *out_can_refresh = (mod::WeatherActive(enable_weather, local_only, allow_online) && is_fetching == 0) ? 1 : 0;
    return VN_OK;
}

// Decides what the weather module does when its settings change.
VN_EXPORT int32_t __stdcall vn_weather_decide_settings_change(
    const mod::VnWeatherSettingsInput* input,
    const char16_t* previous_city, int32_t previous_city_length,
    const char16_t* new_city, int32_t new_city_length,
    int32_t* out_action, uint8_t* out_should_clear) noexcept {
    if (!input || !out_action || !out_should_clear || previous_city_length < 0 || new_city_length < 0 ||
        (previous_city_length > 0 && !previous_city) || (new_city_length > 0 && !new_city))
        return VN_ERR_INVALID_ARG;

    const bool was_active = mod::WeatherActive(input->previous_enable_weather, input->previous_local_only, input->previous_allow_online);
    const bool now_active = mod::WeatherActive(input->enable_weather, input->local_only, input->allow_online);
    *out_should_clear = 0;

    if (!now_active) {
        *out_action = mod::kWeatherDisable;
        *out_should_clear = (was_active || input->is_running != 0 || input->is_fetching != 0) ? 1 : 0;
        return VN_OK;
    }
    if (input->is_running == 0) {
        *out_action = mod::kWeatherStart;
        return VN_OK;
    }
    const bool same_city = mod::CitiesEqual(
        std::u16string_view(previous_city ? previous_city : u"", static_cast<size_t>(previous_city_length)),
        std::u16string_view(new_city ? new_city : u"", static_cast<size_t>(new_city_length)));
    *out_action = (!was_active || !same_city) ? mod::kWeatherRefresh : mod::kWeatherNone;
    return VN_OK;
}

} // extern "C"
