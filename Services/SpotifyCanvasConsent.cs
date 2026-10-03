using VNotch.Models;

namespace VNotch.Services;

// A feature preference or imported configuration is not acknowledgement of the notice.
internal static class SpotifyCanvasConsent
{
    internal const int CurrentNoticeVersion = 1;

    internal static bool HasAccepted(NotchSettings settings) =>
        settings.SpotifyCanvasConsentVersion == CurrentNoticeVersion;

    internal static bool TryEnable(NotchSettings settings, Func<bool> confirm)
    {
        if (!HasAccepted(settings) && !confirm())
        {
            Revoke(settings);
            return false;
        }

        settings.SpotifyCanvasConsentVersion = CurrentNoticeVersion;
        settings.EnableSpotifyCanvas = true;
        settings.AllowOnlineCanvas = true;
        return true;
    }

    internal static void Revoke(NotchSettings settings)
    {
        settings.SpotifyCanvasConsentVersion = 0;
        settings.EnableSpotifyCanvas = false;
        settings.AllowOnlineCanvas = false;
    }
}
