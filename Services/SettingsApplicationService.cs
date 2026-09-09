using System;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public sealed class SettingsApplicationService : ISettingsApplicationService
{
    private readonly ISettingsService _settingsService;
    private readonly IStartupManager _startupManager;

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

        await _settingsService.SaveAsync(settings).ConfigureAwait(false);
        _startupManager.SetAutoStart(settings.AutoStart);
        SettingsApplied?.Invoke(this, settings);
    }

    public async Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(
        string filePath,
        NotchSettings? currentSettings = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));
        ct.ThrowIfCancellationRequested();

        var result = _settingsService.ImportSettingsFromFile(filePath, currentSettings);
        await ApplyAsync(result.Settings, ct).ConfigureAwait(false);
        return result;
    }

    public void Export(string filePath, NotchSettings settings)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty", nameof(filePath));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        _settingsService.ExportSettingsToFile(filePath, settings);
    }

    public bool IsAutoStartEnabled() => _startupManager.IsAutoStartEnabled();
}
