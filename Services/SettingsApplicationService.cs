using System;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public sealed class SettingsApplicationService : ISettingsApplicationService
{
    private static bool IsValidFilePath(string? filePath) =>
        filePath != null && filePath.Any(c => c > ' ' && c != '\u00a0' && c != '\ufeff');

    private readonly ISettingsService _settingsService;
    private readonly IStartupManager _startupManager;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    public event EventHandler<NotchSettings>? SettingsApplied;

    public SettingsApplicationService(ISettingsService settingsService, IStartupManager? startupManager = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _startupManager = startupManager ?? new WindowsStartupManager();
    }

    public NotchSettings Load() => _settingsService.Load();

    public async Task ApplyAsync(NotchSettings settings, CancellationToken ct = default)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        ct.ThrowIfCancellationRequested();

        var snapshot = settings.Clone();
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool previousStartup = _startupManager.IsAutoStartEnabled();
            bool startupChanged = previousStartup != snapshot.AutoStart;
            if (startupChanged) _startupManager.SetAutoStart(snapshot.AutoStart);
            try { await _settingsService.SaveAsync(snapshot).ConfigureAwait(false); }
            catch (Exception saveError)
            {
                try { if (startupChanged) _startupManager.SetAutoStart(previousStartup); }
                catch (Exception rollbackError) { throw new AggregateException(saveError, rollbackError); }
                throw;
            }
            SettingsApplied?.Invoke(this, snapshot);
        }
        finally { _applyGate.Release(); }
    }

    public async Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(
        string filePath,
        NotchSettings? currentSettings = null,
        CancellationToken ct = default)
    {
        if (!IsValidFilePath(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));
        ct.ThrowIfCancellationRequested();

        var result = await Task.Run(() => _settingsService.ImportSettingsFromFile(filePath, currentSettings), ct).ConfigureAwait(false);
        await ApplyAsync(result.Settings, ct).ConfigureAwait(false);
        return result;
    }

    public void Export(string filePath, NotchSettings settings)
    {
        if (!IsValidFilePath(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        _settingsService.ExportSettingsToFile(filePath, settings);
    }

    public bool IsAutoStartEnabled() => _startupManager.IsAutoStartEnabled();
}
