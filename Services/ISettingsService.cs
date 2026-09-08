using VNotch.Models;

namespace VNotch.Services;

public interface ISettingsService
{
    NotchSettings Load();

    void Save(NotchSettings settings);

    Task SaveAsync(NotchSettings settings)
    {
        Save(settings);
        return Task.CompletedTask;
    }

    void ExportSettingsToFile(string filePath, NotchSettings settings);

    (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null);
}
