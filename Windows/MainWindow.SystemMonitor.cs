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
            Shelf = new SystemMonitorShelfViewRefs(ShelfSysMonSection, ShelfSysMonCpuText,
                ShelfSysMonCpuBar, ShelfSysMonRamText, ShelfSysMonRamBar,
                ShelfSysMonNetDownText, ShelfSysMonNetUpText)
        };

        _systemMonitorPresenter = new SystemMonitorPresenter(_systemMonitorModule, new DispatcherService(Dispatcher), refs);
    }

    internal void DisposeSystemMonitorPresenter()
    {
        _systemMonitorPresenter?.Dispose();
        _systemMonitorPresenter = null;
    }

    private double _lastNetDownBytesPerSec => _systemMonitorPresenter?.LastNetDownBytesPerSec ?? 0;
    private double _lastNetUpBytesPerSec => _systemMonitorPresenter?.LastNetUpBytesPerSec ?? 0;

    #endregion
}
