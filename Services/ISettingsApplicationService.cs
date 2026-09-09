using System;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Models;

namespace VNotch.Services;

public interface ISettingsApplicationService
{
    NotchSettings Load();

    Task ApplyAsync(NotchSettings settings, CancellationToken ct = default);

    Task<(NotchSettings Settings, bool RequiresRestart)> ImportAsync(string filePath, NotchSettings? currentSettings = null, CancellationToken ct = default);

    void Export(string filePath, NotchSettings settings);

    bool IsAutoStartEnabled();

    event EventHandler<NotchSettings>? SettingsApplied;
}
