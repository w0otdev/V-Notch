using VNotch.Models;

namespace VNotch.Services;

public sealed partial class SettingsApplicationService
{
    // The shell supplies adapters for its lifecycle-owned features; this service owns change policy.
    internal sealed record RuntimeTargets(
        Action<NotchSettings> Clipboard,
        Action<NotchSettings> NetworkPrivacy,
        Action<NotchSettings> AiUsage,
        Action<NotchSettings> Translation,
        Action<NotchSettings> Weather,
        Action<NotchSettings> Canvas);

    internal readonly record struct RuntimeChanges(bool Weather, bool Canvas, bool Subtitles);

    internal static RuntimeChanges ApplyRuntimeSettings(NotchSettings previous, NotchSettings next, RuntimeTargets targets)
    {
        var changes = new RuntimeChanges(
            previous.EnableWeather != next.EnableWeather || previous.AllowOnlineWeather != next.AllowOnlineWeather ||
            previous.EnableLocalOnlyMode != next.EnableLocalOnlyMode || !string.Equals(previous.ManualCity, next.ManualCity, StringComparison.Ordinal),
            previous.EnableSpotifyCanvas != next.EnableSpotifyCanvas || previous.AllowOnlineCanvas != next.AllowOnlineCanvas ||
            previous.SpotifyCanvasConsentVersion != next.SpotifyCanvasConsentVersion || previous.EnableLocalOnlyMode != next.EnableLocalOnlyMode ||
            !string.Equals(previous.SpotifySpDc, next.SpotifySpDc, StringComparison.Ordinal),
            previous.IgnoreYouTubeAutoSubtitles != next.IgnoreYouTubeAutoSubtitles ||
            !string.Equals(previous.SubtitlePriority ?? "", next.SubtitlePriority ?? "", StringComparison.Ordinal));
        // Privacy changes take effect before a feature can start new network work.
        targets.NetworkPrivacy(next.Clone());
        targets.Clipboard(next.Clone());
        targets.AiUsage(next.Clone());
        targets.Translation(next.Clone());
        if (changes.Weather) targets.Weather(next.Clone());
        if (changes.Canvas) targets.Canvas(next.Clone());
        return changes;
    }
}
