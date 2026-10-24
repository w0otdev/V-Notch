#include "vnotch_common.h"

using namespace vnotch;

namespace vnotch::services
{
    class WeatherConditionFormatter final
    {
    public:
        static int32_t vn_weather_get_localization_key_impl(int32_t weather_code, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
        {
    std::u16string_view key = u"weather.condition.unknown";
    switch (weather_code)
    {
        case 0: key = u"weather.condition.clear"; break;
        case 1: key = u"weather.condition.mainlyClear"; break;
        case 2: key = u"weather.condition.partlyCloudy"; break;
        case 3: key = u"weather.condition.cloudy"; break;
        case 45:
        case 48: key = u"weather.condition.fog"; break;
        case 51:
        case 53:
        case 55: key = u"weather.condition.drizzle"; break;
        case 56:
        case 57: key = u"weather.condition.freezingDrizzle"; break;
        case 61:
        case 63:
        case 65: key = u"weather.condition.rain"; break;
        case 66:
        case 67: key = u"weather.condition.freezingRain"; break;
        case 71:
        case 73:
        case 75: key = u"weather.condition.snow"; break;
        case 77: key = u"weather.condition.snowGrains"; break;
        case 80:
        case 81:
        case 82: key = u"weather.condition.rainShowers"; break;
        case 85:
        case 86: key = u"weather.condition.snowShowers"; break;
        case 95:
        case 96:
        case 99: key = u"weather.condition.thunderstorm"; break;
        default: break;
    }
    return copy_out_u16(key, out_buf, out_cap, out_len);
}

    };
} // namespace vnotch::services

VN_EXPORT int32_t vn_weather_get_localization_key(int32_t weather_code, char16_t* out_buf, int32_t out_cap, int32_t* out_len) noexcept
{
    return vnotch::services::WeatherConditionFormatter::vn_weather_get_localization_key_impl(weather_code, out_buf, out_cap, out_len);
}

