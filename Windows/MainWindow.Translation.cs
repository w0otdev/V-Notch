using VNotch.Controllers;

namespace VNotch;

public partial class MainWindow
{
    private LiveTranslationController? _liveTranslation;
    private void InitializeLiveTranslation()
    {
        _liveTranslation ??= new LiveTranslationController(Dispatcher, () => OpenAppSettings(showTranslation: true));
        _liveTranslation.ApplySettings(_settings);
    }
}
