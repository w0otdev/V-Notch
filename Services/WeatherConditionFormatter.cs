using System.Runtime.InteropServices;

namespace VNotch.Services;

public static partial class WeatherConditionFormatter
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_weather_get_localization_key(int weatherCode, char* outBuf, int outCap, out int outLen);

    public static string Format(int weatherCode) => Loc.Get(GetLocalizationKey(weatherCode));

    internal static unsafe string GetLocalizationKey(int weatherCode)
    {
        char* buf = stackalloc char[64];
        _ = vn_weather_get_localization_key(weatherCode, buf, 64, out int len);
        return len > 0 ? new string(buf, 0, len) : "weather.condition.unknown";
    }
}
