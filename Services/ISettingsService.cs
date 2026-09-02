using VNotch.Models;

namespace VNotch.Services;

public interface ISettingsService
{
    NotchSettings Load();

    void Save(NotchSettings settings);

    void ExportSettingsToFile(string filePath, NotchSettings settings);

    (NotchSettings Settings, bool RequiresRestart) ImportSettingsFromFile(string filePath, NotchSettings? currentSettings = null);
}
