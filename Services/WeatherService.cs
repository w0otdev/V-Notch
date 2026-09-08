using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

#pragma warning disable S1075 // Public Weather and Geolocation API endpoints
public sealed class WeatherService : IWeatherService
{
    private const string LogCategory = "WEATHER";
    private readonly HttpClient _http;

    public WeatherService() : this(CreateHttpClient()) { }

    internal WeatherService(HttpClient httpClient) => _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<WeatherInfo?> GetCurrentWeatherAsync(string? manualCity = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var location = await ResolveLocationAsync(manualCity, cancellationToken).ConfigureAwait(false);
            if (location is null) return null;

            var (lat, lon, city) = location.Value;
            string url =
                "https://api.open-meteo.com/v1/forecast" +
                $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                "&current=temperature_2m,weather_code,is_day" +
                "&daily=temperature_2m_max,temperature_2m_min" +
                "&timezone=auto&forecast_days=1";

            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"Open-Meteo HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var info = ParseForecastJson(json, city);
            if (info != null)
            {
                RuntimeLog.Log(LogCategory, $"{info.City} {info.Temperature}° {info.Condition} (H:{info.High} L:{info.Low})");
            }
            return info;
        }
        catch (OperationCanceledException)
        {
            RuntimeLog.Log(LogCategory, "Request timed out or was cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"Error: {ex.Message}");
            return null;
        }
    }

    private async Task<(double lat, double lon, string city)?> ResolveLocationAsync(string? manualCity, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(manualCity))
        {
            var coords = await ResolveCityCoordinatesAsync(manualCity, token).ConfigureAwait(false);
            if (coords is null)
            {
                RuntimeLog.Log(LogCategory, $"Could not resolve manual city: {manualCity}");
                return null;
            }
            return coords;
        }

        var location = await TryIpWhoIsAsync(token).ConfigureAwait(false);
        if (location is null)
        {
            RuntimeLog.Log(LogCategory, "Could not resolve location. Weather unavailable.");
            return null;
        }
        return location;
    }

    private static WeatherInfo? ParseForecastJson(string json, string city)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("current", out var current))
        {
            RuntimeLog.Log(LogCategory, "Open-Meteo response missing 'current'.");
            return null;
        }

        double temp = current.GetProperty("temperature_2m").GetDouble();
        int weatherCode = current.GetProperty("weather_code").GetInt32();
        bool isDay = current.TryGetProperty("is_day", out var isDayProp) && isDayProp.GetInt32() == 1;

        var (high, low) = ExtractDailyTemperatures(root, temp);

        return new WeatherInfo
        {
            City = city,
            Temperature = RoundTemperature(temp),
            High = RoundTemperature(high),
            Low = RoundTemperature(low),
            WeatherCode = weatherCode,
            Condition = DescribeWeatherCode(weatherCode),
            IsDay = isDay
        };
    }

    private static (double high, double low) ExtractDailyTemperatures(JsonElement root, double fallbackTemp)
    {
        double high = fallbackTemp;
        double low = fallbackTemp;
        if (root.TryGetProperty("daily", out var daily))
        {
            if (daily.TryGetProperty("temperature_2m_max", out var maxArr) && maxArr.GetArrayLength() > 0)
                high = maxArr[0].GetDouble();
            if (daily.TryGetProperty("temperature_2m_min", out var minArr) && minArr.GetArrayLength() > 0)
                low = minArr[0].GetDouble();
        }
        return (high, low);
    }

    private async Task<(double lat, double lon, string city)?> ResolveCityCoordinatesAsync(string city, CancellationToken token)
    {
        try
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search" +
                         $"?name={Uri.EscapeDataString(city)}" +
                         "&count=1&language=en&format=json";

            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"Geocoding HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                RuntimeLog.Log(LogCategory, $"Geocoding: no results for '{city}'");
                return null;
            }

            var first = results[0];
            double lat = first.GetProperty("latitude").GetDouble();
            double lon = first.GetProperty("longitude").GetDouble();
            string resolvedCity = first.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString() ?? city
                : city;

            return (lat, lon, resolvedCity);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"Geocoding failed: {ex.Message}");
            return null;
        }
    }

    private async Task<(double, double, string)?> TryIpWhoIsAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync("https://ipwho.is/", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"ipwho.is HTTP {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var successProp) &&
                successProp.ValueKind == JsonValueKind.False)
            {
                string reason = root.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString() ?? "unknown"
                    : "unknown";
                RuntimeLog.Log(LogCategory, $"ipwho.is error: {reason}");
                return null;
            }

            if (!root.TryGetProperty("latitude", out var latProp) ||
                !root.TryGetProperty("longitude", out var lonProp))
                return null;

            double lat = latProp.GetDouble();
            double lon = lonProp.GetDouble();
            string city = root.TryGetProperty("city", out var cityProp)
                ? cityProp.GetString() ?? string.Empty
                : string.Empty;

            return (lat, lon, city);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"ipwho.is failed: {ex.Message}");
            return null;
        }
    }

    public static string DescribeWeatherCode(int code) => code switch
    {
        0 => "Clear",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm",
        _ => "—"
    };

    private static int RoundTemperature(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("V-Notch/1.7.0 (https://github.com/rainaku/V-Notch)");
        return client;
    }
}
