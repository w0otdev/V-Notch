using System.Windows;
using VNotch.Presenters;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region System Monitor Widget

    private SystemMonitorPresenter? _systemMonitorPresenter;

    internal void InitializeSystemMonitorPresenter()
    {
        if (_systemMonitorPresenter != null) return;

        var refs = new SystemMonitorViewRefs(
            SysMonCpuValueText,
            SysMonCpuBar,
            SysMonRamValueText,
            SysMonRamBar,
            SysMonNetDownText,
            SysMonNetUpText)
        {
            Section = SystemMonitorWidgetContent
        };

        _systemMonitorPresenter = new SystemMonitorPresenter(_systemMonitorModule, new DispatcherService(Dispatcher), refs);
        SystemMonitorWidgetContent.IsVisibleChanged += SystemMonitorWidget_IsVisibleChanged;
    }

    internal void DisposeSystemMonitorPresenter()
    {
        SystemMonitorWidgetContent.IsVisibleChanged -= SystemMonitorWidget_IsVisibleChanged;
        _systemMonitorPresenter?.Dispose();
        _systemMonitorPresenter = null;
    }

    private void SystemMonitorWidget_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateSystemMonitorActivity();

    private void UpdateSystemMonitorActivity()
    {
        // Effective visibility includes the parent page and window, so changing
        // widgets, navigating away, collapsing and hiding all release sampling.
        bool needed = _isDebugModeEnabled || SystemMonitorWidgetContent.IsVisible;
        if (needed && !_systemMonitorModule.IsRunning)
            _systemMonitorModule.Start();
        else if (!needed && _systemMonitorModule.IsRunning)
            _systemMonitorModule.Stop();
    }

    private double _lastNetDownBytesPerSec => _systemMonitorPresenter?.LastNetDownBytesPerSec ?? 0;
    private double _lastNetUpBytesPerSec => _systemMonitorPresenter?.LastNetUpBytesPerSec ?? 0;

    #endregion
}
