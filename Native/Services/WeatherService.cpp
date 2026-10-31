#include "vnotch_common.h"
#include <cmath>
#include <windows.h>

namespace vnotch::services
{

class WeatherService final
{
    static bool IsControlChar(char16_t ch) noexcept
    {
        if (ch <= 0x001Fu || (ch >= 0x007Fu && ch <= 0x009Fu))
        {
            return true;
        }
        if (ch < 0x0080u)
        {
            return false;
        }
        WORD type = 0;
        WCHAR wch = static_cast<WCHAR>(ch);
        if (GetStringTypeW(CT_CTYPE1, &wch, 1, &type))
        {
            return (type & C1_CNTRL) != 0;
        }
        return false;
    }

public:
    static bool CleanCity(std::u16string_view city, std::u16string& out_cleaned)
    {
        out_cleaned.clear();
        if (vnotch::trim_u16(city).empty())
        {
            return false;
        }
        std::u16string temp;
        temp.reserve(city.size());
        for (char16_t ch : city)
        {
            if (!IsControlChar(ch))
            {
                temp.push_back(ch);
            }
        }
        std::u16string_view trimmed = vnotch::trim_u16(temp);
        if (trimmed.empty())
        {
            return false;
        }
        out_cleaned.assign(trimmed.data(), trimmed.size());
        return true;
    }

    static std::u16string_view DescribeWeatherCode(int32_t code) noexcept
    {
        switch (code)
        {
        case 0: return u"Clear";
        case 1: return u"Mainly clear";
        case 2: return u"Partly cloudy";
        case 3: return u"Cloudy";
        case 45:
        case 48: return u"Fog";
        case 51:
        case 53:
        case 55: return u"Drizzle";
        case 56:
        case 57: return u"Freezing drizzle";
        case 61:
        case 63:
        case 65: return u"Rain";
        case 66:
        case 67: return u"Freezing rain";
        case 71:
        case 73:
        case 75: return u"Snow";
        case 77: return u"Snow grains";
        case 80:
        case 81:
        case 82: return u"Rain showers";
        case 85:
        case 86: return u"Snow showers";
        case 95:
        case 96:
        case 99: return u"Thunderstorm";
        default: return u"\u2014";
        }
    }

    static int32_t RoundTemperature(double value) noexcept
    {
        if (!std::isfinite(value)) return 0;
        return static_cast<int32_t>(std::clamp(std::round(value), -1000.0, 1000.0));
    }
};

} // namespace vnotch::services

VN_EXPORT int32_t vn_weather_service_clean_city(
    const char16_t* city_ptr,
    int32_t city_len,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len,
    uint8_t* out_has_value) noexcept
{
    if (out_has_value == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    try
    {
        std::u16string cleaned;
        bool ok = vnotch::services::WeatherService::CleanCity(
            vnotch::make_u16_view(city_ptr, city_len),
            cleaned);
        *out_has_value = ok ? 1u : 0u;
        if (!ok)
        {
            if (out_len) *out_len = 0;
            return VN_OK;
        }
        return vnotch::copy_out_u16(cleaned, out_buf, out_cap, out_len);
    }
    catch (...)
    {
        *out_has_value = 0u;
        return VN_ERR_FAIL;
    }
}

VN_EXPORT int32_t vn_weather_service_describe_code(
    int32_t code,
    char16_t* out_buf,
    int32_t out_cap,
    int32_t* out_len) noexcept
{
    std::u16string_view desc = vnotch::services::WeatherService::DescribeWeatherCode(code);
    return vnotch::copy_out_u16(desc, out_buf, out_cap, out_len);
}

VN_EXPORT int32_t vn_weather_service_round_temperature(
    double value,
    int32_t* out_rounded) noexcept
{
    if (out_rounded == nullptr)
    {
        return VN_ERR_INVALID_ARG;
    }
    *out_rounded = vnotch::services::WeatherService::RoundTemperature(value);
    return VN_OK;
}
