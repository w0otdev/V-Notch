using System;
using System.Runtime.InteropServices;
using VNotch.Models;

namespace VNotch.Services;

internal static partial class SpotifyCanvasConsent
{
    internal const int CurrentNoticeVersion = 1;

    internal static bool HasAccepted(NotchSettings settings)
    {
        vn_spotify_canvas_consent_has_accepted(settings.SpotifyCanvasConsentVersion, out byte accepted);
        return accepted != 0;
    }

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

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_spotify_canvas_consent_has_accepted(
        int consent_version, out byte out_accepted);
}
