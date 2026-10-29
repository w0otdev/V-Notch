using System;
using System.Runtime.InteropServices;
using System.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

public class WeatherUpdateEventArgs : EventArgs
{
    public WeatherInfo? Weather { get; init; }
}

public partial class WeatherModule : NotchModuleBase
{
    private const string NativeDll = "VNotch.Native.dll";

    private const int ActionDisable = 1;
    private const int ActionStart = 2;
    private const int ActionRefresh = 3;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnWeatherSettingsInput
    {
        public byte PreviousEnableWeather;
        public byte PreviousLocalOnly;
        public byte PreviousAllowOnline;
        public byte EnableWeather;
        public byte LocalOnly;
        public byte AllowOnline;
        public byte IsRunning;
        public byte IsFetching;
    }

    [LibraryImport(NativeDll, EntryPoint = "vn_weather_can_refresh")]
    private static partial int NativeCanRefresh(byte enableWeather, byte localOnly, byte allowOnline, byte isFetching, out byte canRefresh);

    [LibraryImport(NativeDll, EntryPoint = "vn_weather_decide_settings_change", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeDecideSettingsChange(
        ref VnWeatherSettingsInput input,
        ReadOnlySpan<char> previousCity, int previousCityLength,
        ReadOnlySpan<char> newCity, int newCityLength,
        out int action, out byte shouldClear);

    private static byte Flag(bool value) => value ? (byte)1 : (byte)0;

    private readonly IWeatherService _weatherService;
    private NotchSettings _settings;
    private bool _isFetching;
    private CancellationTokenSource? _cts;
    private int _requestVersion;
    private WeatherInfo? _lastWeather;

    public WeatherModule(IWeatherService weatherService, ISettingsService settingsService)
    {
        _weatherService = weatherService ?? throw new ArgumentNullException(nameof(weatherService));
        ArgumentNullException.ThrowIfNull(settingsService);
        _settings = settingsService.Load().Clone();
    }

    public override string ModuleName => "Weather";

    public override TimeSpan? TickInterval => TimeSpan.FromMinutes(15);

    public event EventHandler<WeatherUpdateEventArgs>? WeatherUpdated;

    public void RetryNow() => _ = RefreshAsync();

    public void RefreshLocalization()
    {
        WeatherUpdated?.Invoke(this, new WeatherUpdateEventArgs { Weather = _lastWeather });
    }

    public void OnSettingsChanged(NotchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var previous = _settings;
        string previousCity = previous.ManualCity ?? string.Empty;
        string newCity = settings.ManualCity ?? string.Empty;
        _settings = settings.Clone();

        var input = new VnWeatherSettingsInput
        {
            PreviousEnableWeather = Flag(previous.EnableWeather),
            PreviousLocalOnly = Flag(previous.EnableLocalOnlyMode),
            PreviousAllowOnline = Flag(previous.AllowOnlineWeather),
            EnableWeather = Flag(settings.EnableWeather),
            LocalOnly = Flag(settings.EnableLocalOnlyMode),
            AllowOnline = Flag(settings.AllowOnlineWeather),
            IsRunning = Flag(IsRunning),
            IsFetching = Flag(_isFetching),
        };
        if (NativeDecideSettingsChange(ref input, previousCity, previousCity.Length, newCity, newCity.Length,
                out int action, out byte shouldClear) != 0)
            return;

        switch (action)
        {
            case ActionDisable:
                Stop();
                CancelPendingRequest();
                if (shouldClear != 0)
                {
                    ClearWeatherData();
                }

                break;
            case ActionStart:
                // Start() performs an immediate tick before starting the timer.
                Start();
                break;
            case ActionRefresh:
                // Weather was just enabled or the city changed: drop the stale request and refresh explicitly.
                CancelPendingRequest();
                _ = RefreshAsync();
                break;
        }
    }
    private static string NormalizeCity(string? city) => city?.Trim() ?? string.Empty;

    private void CancelPendingRequest()
    {
        _requestVersion++;
        var cts = _cts;
        _cts = null;
        _isFetching = false;

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Intentionally ignored.
        }
        finally
        {
            try { cts?.Dispose(); } catch (ObjectDisposedException) { /* Intentionally ignored. */ }
        }
    }

    protected override void OnStop()
    {
        CancelPendingRequest();
        base.OnStop();
    }

    private void ClearWeatherData()
    {
        PublishWeather(null);
    }

    private void PublishWeather(WeatherInfo? weather)
    {
        _lastWeather = weather;
        WeatherUpdated?.Invoke(this, new WeatherUpdateEventArgs { Weather = weather });
    }

    protected override void OnTick()
    {
        _ = RefreshAsync();
    }

    private async System.Threading.Tasks.Task RefreshAsync()
    {
        var settings = _settings;
        if (NativeCanRefresh(Flag(settings.EnableWeather), Flag(settings.EnableLocalOnlyMode), Flag(settings.AllowOnlineWeather),
                Flag(_isFetching), out byte canRefresh) != 0 || canRefresh == 0)
        {
            return;
        }

        _isFetching = true;
        int requestVersion = ++_requestVersion;
        var requestCts = new CancellationTokenSource();
        _cts = requestCts;

        try
        {
            string manualCity = NormalizeCity(settings.ManualCity);
            var weather = await _weatherService
                .GetCurrentWeatherAsync(manualCity.Length == 0 ? null : manualCity, requestCts.Token)
                .ConfigureAwait(true);

            if (requestVersion == _requestVersion)
            {
                // A null update tells the UI that the provider could not return data.
                PublishWeather(weather);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when weather is disabled or its location changes.
        }
        catch (Exception ex)
        {
            if (requestVersion == _requestVersion)
            {
                RuntimeLog.Log("MODULE-Weather", $"Refresh failed: {ex.Message}");
                PublishWeather(null);
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, requestCts))
            {
                _cts = null;
            }
            if (requestVersion == _requestVersion)
            {
                _isFetching = false;
            }
            try { requestCts.Dispose(); } catch (ObjectDisposedException) { /* Intentionally ignored. */ }
        }
    }

    protected override void OnDispose()
    {
        CancelPendingRequest();
        base.OnDispose();
    }
}
