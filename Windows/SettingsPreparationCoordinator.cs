using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

internal sealed class SettingsPreparationCoordinator(Dispatcher dispatcher, Func<NotchSettings> settings,
    Func<bool, SettingsWindow> createWindow, Func<bool> hotkeyRegistered, Func<bool> closed, Func<bool> animating)
{
    private SettingsWindow? _preparedSettingsWindow;
    internal SettingsWindow? Active { get; set; }
    private NotchSettings? _preparedSettingsSnapshot;
    private bool _settingsPreparationScheduled;
    private int _settingsPreparationGeneration;
    private UiScheduledTimer? _settingsPreparationExpiry;

    internal SettingsWindow? Prepared => _preparedSettingsWindow;
    internal bool HasSnapshot => _preparedSettingsSnapshot != null;
    internal bool HasExpiry => _settingsPreparationExpiry != null;

    internal void Schedule()
    {
        if (closed() || Active != null) return;
        if (_preparedSettingsWindow != null)
        {
            RestartSettingsPreparationExpiry();
            return;
        }
        if (_settingsPreparationScheduled) return;
        _settingsPreparationScheduled = true;
        int generation = _settingsPreparationGeneration;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            PrepareAsync(generation).SafeFireAndForget("SETTINGS-PREPARE")));
    }

    internal async Task PrepareAsync(int generation)
    {
        if (!await WaitForSettingsPreparationIdleAsync(generation)) return;
        SettingsWindow? prepared = null;
        try
        {
            // Both stages run after the notch's entrance. Yield between XAML
            // construction and native template realization so input can run.
            _preparedSettingsSnapshot = settings().Clone();
            prepared = createWindow(true);
            _preparedSettingsWindow = prepared;
            await dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle);
            if (!await WaitForSettingsPreparationIdleAsync(generation) || !ReferenceEquals(prepared, _preparedSettingsWindow)) return;
            await prepared.PrepareForFirstShowAsync();
            if (generation == _settingsPreparationGeneration && ReferenceEquals(prepared, _preparedSettingsWindow))
                RestartSettingsPreparationExpiry();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(prepared, _preparedSettingsWindow))
            {
                _preparedSettingsWindow = null;
                _preparedSettingsSnapshot = null;
                _settingsPreparationScheduled = false;
                prepared?.Close();
            }
            RuntimeLog.Warn("FIRST-OPEN", $"Settings preparation skipped: {ex.GetType().Name}");
        }
    }

    private void RestartSettingsPreparationExpiry()
    {
        _settingsPreparationExpiry?.Dispose();
        _settingsPreparationExpiry = new UiScheduledTimer { Interval = TimeSpan.FromSeconds(30) };
        _settingsPreparationExpiry.Tick += (_, _) => Release();
        _settingsPreparationExpiry.Start();
    }

    internal void Release()
    {
        _settingsPreparationGeneration++;
        _settingsPreparationScheduled = false;
        _settingsPreparationExpiry?.Dispose();
        _settingsPreparationExpiry = null;
        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        _preparedSettingsSnapshot = null;
        prepared?.Close();
    }

    private async Task<bool> WaitForSettingsPreparationIdleAsync(int generation)
    {
        // ApplicationIdle can run between animation frames. A click immediately
        // after startup must not compete with Settings XAML or native realization.
        // Recheck each stage because input can arrive while preparation yields.
        while (!closed() && generation == _settingsPreparationGeneration && Active == null)
        {
            if (!animating()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    internal SettingsWindow Take()
    {
        // Cancel queued stages before taking ownership. A user opening Settings
        // while its hidden realization yields must never have it hidden again.
        _settingsPreparationGeneration++;
        _settingsPreparationScheduled = false;
        _settingsPreparationExpiry?.Dispose();
        _settingsPreparationExpiry = null;
        var prepared = _preparedSettingsWindow;
        _preparedSettingsWindow = null;
        var snapshot = _preparedSettingsSnapshot;
        _preparedSettingsSnapshot = null;
        if (prepared == null) return createWindow(false);

        prepared.ActivatePreparedWindow(
            settings(),
            snapshotMatches: snapshot?.ValueEquals(settings()) == true,
            hotkeyRegistered());
        return prepared;
    }

    internal void Close()
    {
        Release();
        var active = Active;
        Active = null;
        active?.Close();
    }
}
