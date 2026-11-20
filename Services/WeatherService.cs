using System;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

#pragma warning disable S1075 // Public Weather and Geolocation API endpoints
public sealed partial class WeatherService : IWeatherService, IDisposable
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_weather_service_clean_city(
        ReadOnlySpan<char> cityPtr,
        int cityLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outHasValue);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_weather_service_describe_code(
        int code,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_weather_service_round_temperature(
        double value,
        out int outRounded);

    private const string LogCategory = "WEATHER";
    private const int MaxWeatherResponseBytes = 512 * 1024; // 512 KB
    private static readonly JsonDocumentOptions WeatherJsonOptions = new() { MaxDepth = 16 };

    private sealed record CachedLocation(double Lat, double Lon, string City, DateTime Expiry);
    private CachedLocation? _cachedLocation;

    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly Dictionary<string, (WeatherInfo Weather, DateTimeOffset Expires)> _forecasts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset Until, int Failures)> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Diagnostics.Metrics.Meter WeatherMetrics = new("VNotch.Weather");
    private static readonly System.Diagnostics.Metrics.Counter<long> Requests = WeatherMetrics.CreateCounter<long>("weather.requests");
    private static readonly System.Diagnostics.Metrics.Counter<long> Timeouts = WeatherMetrics.CreateCounter<long>("weather.timeouts");
    private static readonly System.Diagnostics.Metrics.Counter<long> RateLimits = WeatherMetrics.CreateCounter<long>("weather.rate_limits");

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public WeatherService() : this(CreateHttpClient()) { _ownsHttpClient = true; }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    internal WeatherService(HttpClient httpClient, TimeProvider? clock = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<WeatherInfo?> GetCurrentWeatherAsync(string? manualCity = null, CancellationToken cancellationToken = default)
    {
        try { await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        try
        {
            string key = CleanCity(manualCity) ?? "";
            if (_forecasts.TryGetValue(key, out var cached) && _clock.GetUtcNow() < cached.Expires) return cached.Weather;
            var weather = await FetchCurrentWeatherAsync(manualCity, cancellationToken).ConfigureAwait(false);
            if (weather != null)
            {
                if (_forecasts.Count >= 8) _forecasts.Remove(_forecasts.MinBy(p => p.Value.Expires).Key);
                _forecasts[key] = (weather, _clock.GetUtcNow().AddMinutes(15));
            }
            return weather;
        }
        finally { _fetchGate.Release(); }
    }

    private async Task<WeatherInfo?> FetchCurrentWeatherAsync(string? manualCity, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            using var requestCts = CreateRequestCancellation(cancellationToken);
            using var response = await SendWeatherRequestAsync(url, requestCts.Token, cancellationToken).ConfigureAwait(false);
            if (response == null) return null;
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"Open-Meteo HTTP {(int)response.StatusCode}");
                return null;
            }

            using var doc = await ReadWeatherBodyAsync(response, "api.open-meteo.com", requestCts.Token, cancellationToken).ConfigureAwait(false);
            if (doc is null) return null;

            var info = ParseForecastJson(doc.RootElement, city);
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

    private async Task<HttpResponseMessage?> SendWeatherRequestAsync(string url, CancellationToken requestToken, CancellationToken callerToken)
    {
        string host = new Uri(url).Host;
        var now = _clock.GetUtcNow();
        if (_cooldowns.TryGetValue(host, out var cooldown) && now < cooldown.Until) return null;
        Requests.Add(1);
        try
        {
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                if ((int)response.StatusCode == 429) RateLimits.Add(1);
                int failures = Math.Min(10, cooldown.Failures + 1);
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.FromMinutes(Math.Min(60, 1 << Math.Min(failures - 1, 6))));
                delay = delay > TimeSpan.FromSeconds(1) ? delay : TimeSpan.FromSeconds(1);
                _cooldowns[host] = (delay < DateTimeOffset.MaxValue - now ? now + delay : DateTimeOffset.MaxValue, failures);
            }
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !callerToken.IsCancellationRequested)
        {
            if (ex is OperationCanceledException) Timeouts.Add(1);
            int failures = Math.Min(10, cooldown.Failures + 1);
            _cooldowns[host] = (now.AddMinutes(Math.Min(60, 1 << Math.Min(failures - 1, 6))), failures);
            throw;
        }
    }

    private async Task<JsonDocument?> ReadWeatherBodyAsync(HttpResponseMessage response, string host,
        CancellationToken requestToken, CancellationToken callerToken)
    {
        try
        {
            var document = await ReadLimitedJsonAsync(response, MaxWeatherResponseBytes, requestToken).ConfigureAwait(false);
            if (document != null) _cooldowns.Remove(host);
            return document;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !callerToken.IsCancellationRequested)
        {
            if (ex is OperationCanceledException) Timeouts.Add(1);
            int failures = Math.Min(10, _cooldowns.GetValueOrDefault(host).Failures + 1);
            _cooldowns[host] = (_clock.GetUtcNow().AddMinutes(Math.Min(60, 1 << Math.Min(failures - 1, 6))), failures);
            throw;
        }
    }

    private CancellationTokenSource CreateRequestCancellation(CancellationToken token)
    {
        // ResponseHeadersRead stops HttpClient's timeout at the headers. Keep
        // the same timeout covering the bounded body read as well.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_http.Timeout);
        return cts;
    }

    internal static Task<string> ReadBoundedStringAsync(
        HttpClient client,
        string url,
        int maxBytes = MaxWeatherResponseBytes,
        CancellationToken ct = default) =>
        SpotifyHttpResponseReader.ReadBoundedStringAsync(client, url, maxBytes, ct);

    internal static Task<JsonDocument?> ReadLimitedJsonAsync(HttpResponseMessage response, int maxBytes, CancellationToken token) =>
        SpotifyHttpResponseReader.ReadLimitedJsonAsync(response, maxBytes, token, WeatherJsonOptions);

    internal static string? CleanCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        ReadOnlySpan<char> inSpan = city.AsSpan();
        Span<char> buffer = city.Length <= 256
            ? stackalloc char[city.Length]
            : new char[city.Length];
        if (vn_weather_service_clean_city(inSpan, inSpan.Length, buffer, buffer.Length, out int outLen, out byte hasVal) == 0 && hasVal != 0)
        {
            return new string(buffer[..outLen]);
        }
        return null;
    }

    private async Task<(double lat, double lon, string city)?> ResolveLocationAsync(string? manualCity, CancellationToken token)
    {
        string? cleanCity = CleanCity(manualCity);

        if (!string.IsNullOrWhiteSpace(cleanCity))
        {
            var coords = await ResolveCityCoordinatesAsync(cleanCity, token).ConfigureAwait(false);
            if (coords is null)
            {
                RuntimeLog.Log(LogCategory, $"Could not resolve manual city: {cleanCity}");
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

    private static WeatherInfo? ParseForecastJson(JsonElement root, string city)
    {

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
        if (!root.TryGetProperty("daily", out var daily) || daily.ValueKind != JsonValueKind.Object)
            return (fallbackTemp, fallbackTemp);

        double ReadFirst(string key) =>
            daily.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array &&
            values.GetArrayLength() > 0 && values[0].ValueKind == JsonValueKind.Number &&
            values[0].TryGetDouble(out var value) && double.IsFinite(value) ? value : fallbackTemp;

        return (ReadFirst("temperature_2m_max"), ReadFirst("temperature_2m_min"));
    }

    private async Task<(double lat, double lon, string city)?> ResolveCityCoordinatesAsync(string city, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search" +
                         $"?name={Uri.EscapeDataString(city)}" +
                         "&count=1&language=en&format=json";

            using var requestCts = CreateRequestCancellation(token);
            using var response = await SendWeatherRequestAsync(url, requestCts.Token, token).ConfigureAwait(false);
            if (response == null) return null;
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"Geocoding HTTP {(int)response.StatusCode}");
                return null;
            }

            using var doc = await ReadWeatherBodyAsync(response, "geocoding-api.open-meteo.com", requestCts.Token, token).ConfigureAwait(false);
            if (doc is null) return null;
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"Geocoding failed: {ex.Message}");
            return null;
        }
    }

    private async Task<(double, double, string)?> TryIpWhoIsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cached = Volatile.Read(ref _cachedLocation);
        if (cached is not null && _clock.GetUtcNow().UtcDateTime < cached.Expiry)
            return (cached.Lat, cached.Lon, cached.City);

        try
        {
            using var requestCts = CreateRequestCancellation(token);
            using var response = await SendWeatherRequestAsync("https://ipwho.is/", requestCts.Token, token).ConfigureAwait(false);
            if (response == null) return null;
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"ipwho.is HTTP {(int)response.StatusCode}");
                return null;
            }

            using var doc = await ReadWeatherBodyAsync(response, "ipwho.is", requestCts.Token, token).ConfigureAwait(false);
            if (doc is null) return null;
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

            Volatile.Write(ref _cachedLocation, new CachedLocation(lat, lon, city, _clock.GetUtcNow().UtcDateTime.AddMinutes(30)));
            return (lat, lon, city);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogCategory, $"ipwho.is failed: {ex.Message}");
            return null;
        }
    }

    public static string DescribeWeatherCode(int code)
    {
        Span<char> buf = stackalloc char[32];
        if (vn_weather_service_describe_code(code, buf, buf.Length, out int len) == 0 && len > 0)
        {
            return new string(buf[..len]);
        }
        return "—";
    }

    private static int RoundTemperature(double value)
    {
        vn_weather_service_round_temperature(value, out int rounded);
        return rounded;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(NetworkPrivacy.Handler(NetworkFeature.Weather))
        {
            Timeout = TimeSpan.FromSeconds(8),
            MaxResponseContentBufferSize = MaxWeatherResponseBytes
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        return client;
    }
}
