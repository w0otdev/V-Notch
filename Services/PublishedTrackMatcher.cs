using System.Runtime.InteropServices;

namespace VNotch.Services;

internal readonly record struct PublishedTrackSnapshot(
    string TrackIdentity,
    string TrackOnlyIdentity,
    string SourceAppId,
    string SessionInstanceKey);

internal static partial class PublishedTrackMatcher
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_published_track_is_same_track(
        char* lastTrackIdPtr, int lastTrackIdLen,
        char* lastTrackOnlyPtr, int lastTrackOnlyLen,
        char* lastAppIdPtr, int lastAppIdLen,
        char* lastSessionKeyPtr, int lastSessionKeyLen,
        char* expTrackPtr, int expTrackLen,
        char* expArtistPtr, int expArtistLen,
        char* expAppIdPtr, int expAppIdLen,
        char* expSessionKeyPtr, int expSessionKeyLen,
        out byte outSame);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_published_track_is_new_for_thumbnail(
        char* lastTrackOnlyPtr, int lastTrackOnlyLen,
        char* curTrackPtr, int curTrackLen,
        out byte outIsNew);

    public static unsafe bool IsSameTrack(
        in PublishedTrackSnapshot last,
        string expectedTrack,
        string expectedArtist,
        string expectedSourceAppId,
        string expectedSessionInstanceKey)
    {
        string lId = last.TrackIdentity ?? string.Empty;
        string lOnly = last.TrackOnlyIdentity ?? string.Empty;
        string lApp = last.SourceAppId ?? string.Empty;
        string lKey = last.SessionInstanceKey ?? string.Empty;
        string eTr = expectedTrack ?? string.Empty;
        string eAr = expectedArtist ?? string.Empty;
        string eApp = expectedSourceAppId ?? string.Empty;
        string eKey = expectedSessionInstanceKey ?? string.Empty;

        fixed (char* pLId = lId)
        fixed (char* pLOnly = lOnly)
        fixed (char* pLApp = lApp)
        fixed (char* pLKey = lKey)
        fixed (char* pETr = eTr)
        fixed (char* pEAr = eAr)
        fixed (char* pEApp = eApp)
        fixed (char* pEKey = eKey)
        {
            _ = vn_published_track_is_same_track(
                pLId, lId.Length,
                pLOnly, lOnly.Length,
                pLApp, lApp.Length,
                pLKey, lKey.Length,
                pETr, eTr.Length,
                pEAr, eAr.Length,
                pEApp, eApp.Length,
                pEKey, eKey.Length,
                out byte same);
            return same != 0;
        }
    }

    public static unsafe bool IsNewTrackForThumbnail(string lastPublishedTrackOnlyIdentity, string currentTrack)
    {
        string lOnly = lastPublishedTrackOnlyIdentity ?? string.Empty;
        string cTr = currentTrack ?? string.Empty;
        fixed (char* pL = lOnly)
        fixed (char* pC = cTr)
        {
            _ = vn_published_track_is_new_for_thumbnail(pL, lOnly.Length, pC, cTr.Length, out byte isNew);
            return isNew != 0;
        }
    }
}
