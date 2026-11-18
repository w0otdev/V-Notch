#include "../vnotch_common.h"
#include <cstdint>
#include <string_view>

// Weather module settings-change policy; module lifecycle lives in the application layer.

namespace vnotch::modules {

#pragma pack(push, 1)
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
