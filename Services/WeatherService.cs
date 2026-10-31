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

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public WeatherService() : this(CreateHttpClient()) { _ownsHttpClient = true; }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    internal WeatherService(HttpClient httpClient) => _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<WeatherInfo?> GetCurrentWeatherAsync(string? manualCity = null, CancellationToken cancellationToken = default)
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
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"Open-Meteo HTTP {(int)response.StatusCode}");
                return null;
            }

            using var doc = await ReadLimitedJsonAsync(response, MaxWeatherResponseBytes, requestCts.Token).ConfigureAwait(false);
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

    private CancellationTokenSource CreateRequestCancellation(CancellationToken token)
    {
        // ResponseHeadersRead stops HttpClient's timeout at the headers. Keep
        // the same timeout covering the bounded body read as well.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(_http.Timeout);
        return cts;
    }

    internal static async Task<string> ReadBoundedStringAsync(
        HttpClient client,
        string url,
        int maxBytes = MaxWeatherResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var length = response.Content.Headers.ContentLength;
        if (length.HasValue && length.Value > maxBytes)
            throw new InvalidOperationException($"Response content length ({length.Value}) exceeds limit ({maxBytes}).");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new System.IO.MemoryStream(length.HasValue ? (int)length.Value : 4096);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int totalRead = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - totalRead + 1)), ct).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
                if (totalRead > maxBytes)
                    throw new InvalidOperationException($"Response body exceeded limit of {maxBytes} bytes.");
                await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(memory.GetBuffer(), 0, totalRead);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<JsonDocument?> ReadLimitedJsonAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        var length = response.Content.Headers.ContentLength;
        if (length.HasValue && length.Value > maxBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new System.IO.MemoryStream(length.HasValue ? (int)length.Value : 4096);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int totalRead = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - totalRead + 1)), token).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
                if (totalRead > maxBytes) return null;
                await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }

            // This is MemoryStream-owned storage, NOT a rented array. The document
            // retains it safely after the stream is disposed until its own disposal.
            return totalRead == 0 ? null : JsonDocument.Parse(memory.GetBuffer().AsMemory(0, totalRead), WeatherJsonOptions);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

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
        token.ThrowIfCancellationRequested();
        try
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search" +
                         $"?name={Uri.EscapeDataString(city)}" +
                         "&count=1&language=en&format=json";

            using var requestCts = CreateRequestCancellation(token);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"Geocoding HTTP {(int)response.StatusCode}");
                return null;
            }

            using var doc = await ReadLimitedJsonAsync(response, MaxWeatherResponseBytes, requestCts.Token).ConfigureAwait(false);
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
        if (cached is not null && DateTime.UtcNow < cached.Expiry)
            return (cached.Lat, cached.Lon, cached.City);

        try
        {
            using var requestCts = CreateRequestCancellation(token);
            using var response = await _http.GetAsync("https://ipwho.is/", HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                RuntimeLog.Log(LogCategory, $"ipwho.is HTTP {(int)response.StatusCode}");
                return null;
            }

            using var doc = await ReadLimitedJsonAsync(response, MaxWeatherResponseBytes, requestCts.Token).ConfigureAwait(false);
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

            Volatile.Write(ref _cachedLocation, new CachedLocation(lat, lon, city, DateTime.UtcNow.AddMinutes(30)));
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
