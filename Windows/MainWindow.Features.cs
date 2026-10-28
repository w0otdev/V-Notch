namespace VNotch;

public partial class MainWindow
{
    private bool _coreModulesStarted;

    private void StartCoreModules()
    {
        _coreModulesStarted = true;
        _batteryModule.Start();
        _bluetoothModule.Start();
        if (_settings.EnableWeather && !_weatherModule.IsRunning)
        {
            _weatherModule.Start();
        }
        ApplyPrivacyIndicatorSettings();
    }

    private void EnsureCalendarFeatureLoaded()
    {
        InitializeCalendarPresenter();
        if (!_calendarModule.IsRunning)
        {
            _calendarModule.Start();
        }
    }

    private void EnsureActiveExpandedWidgetFeatureLoaded()
    {
        if (!_isExpanded)
        {
            return;
        }

        if (_settings.EnableWeather && !_weatherModule.IsRunning)
        {
            _weatherModule.Start();
        }

        if (IsSystemMonitorWidgetMode && !_systemMonitorModule.IsRunning)
        {
            _systemMonitorModule.Start();
        }
    }
}
