using VNotch.Controllers;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private LiveTranslationController? _liveTranslation;
    private void InitializeLiveTranslation()
    {
        _liveTranslation ??= new LiveTranslationController(
            Dispatcher,
            () => OpenAppSettings(showTranslation: true),
            saveLanguages: (source, target) =>
            {
                _settings.TranslationSourceLanguage = source;
                _settings.TranslationTargetLanguage = target;
                _settingsService.SaveAsync(_settings).SafeFireAndForget("SETTINGS-SAVE");
            });
        _liveTranslation.ApplySettings(_settings);
    }
}
