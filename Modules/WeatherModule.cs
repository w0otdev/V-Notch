using System;
using System.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch.Modules;

public class WeatherUpdateEventArgs : EventArgs
{
    public WeatherInfo? Weather { get; init; }
}

public class WeatherModule : NotchModuleBase
{
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

    /// <summary>
    /// Replays the latest provider data so the UI can reformat it after a locale change
    /// without making another network request.
    /// </summary>
    public void RefreshLocalization()
    {
        WeatherUpdated?.Invoke(this, new WeatherUpdateEventArgs { Weather = _lastWeather });
    }

    /// <summary>
    /// Applies weather settings immediately, including unsaved settings-window previews.
    /// </summary>
    public void OnSettingsChanged(NotchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool wasEnabled = _settings.EnableWeather && !_settings.EnableLocalOnlyMode;
        string previousCity = NormalizeCity(_settings.ManualCity);
        string newCity = NormalizeCity(settings.ManualCity);
        _settings = settings.Clone();

        if (!settings.EnableWeather || settings.EnableLocalOnlyMode)
        {
            bool shouldClear = wasEnabled || IsRunning || _isFetching;
            Stop();
            CancelPendingRequest();
            if (shouldClear)
            {
                ClearWeatherData();
            }

            return;
        }

        if (!IsRunning)
        {
            // Start() performs an immediate tick before starting the timer.
            Start();
            return;
        }

        if (!wasEnabled || !string.Equals(previousCity, newCity, StringComparison.OrdinalIgnoreCase))
        {
            // The host may already have started this module while weather was disabled.
            // Refresh explicitly when it is enabled, and whenever the city changes.
            CancelPendingRequest();
            _ = RefreshAsync();
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
        }
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
        if (!settings.EnableWeather || settings.EnableLocalOnlyMode || _isFetching)
        {
            return;
        }

        _isFetching = true;
        int requestVersion = ++_requestVersion;
        using var requestCts = new CancellationTokenSource();
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
            if (requestVersion == _requestVersion)
            {
                _cts = null;
                _isFetching = false;
            }
        }
    }

    protected override void OnDispose()
    {
        CancelPendingRequest();
        base.OnDispose();
    }
}
