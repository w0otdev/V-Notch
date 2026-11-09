namespace VNotch;

public partial class SettingsWindow
{
    internal void MarkSpotlightIntroSeen()
    {
        _settings.HasSeenSpotlightIntro = true;
        _editSession.CommitSpotlightIntroSeen();
    }
}
