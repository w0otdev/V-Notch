using System.Windows.Media;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal static class SpotlightSolidIcons
{
    private static Geometry Freeze(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static readonly Geometry Settings = Freeze("M10 2 L14 2 L14.6 5 L16 5.8 L19 4.8 L21 8.2 L18.7 10.2 L18.7 13.8 L21 15.8 L19 19.2 L16 18.2 L14.6 19 L14 22 L10 22 L9.4 19 L8 18.2 L5 19.2 L3 15.8 L5.3 13.8 L5.3 10.2 L3 8.2 L5 4.8 L8 5.8 L9.4 5 Z M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 Z");
    private static readonly Geometry Restart = Freeze("M4 4 L4 9 L9 9 L7.2 7.2 A7 7 0 1 1 5 12 L2 12 A10 10 0 1 0 5.1 5.1 Z");
    private static readonly Geometry Power = Freeze("M10.5 2 L13.5 2 L13.5 12 L10.5 12 Z M6 4 L8 6.5 A7 7 0 1 0 16 6.5 L18 4 A10 10 0 1 1 6 4 Z");
    private static readonly Geometry Lock = Freeze("M7 10 L7 7 A5 5 0 0 1 17 7 L17 10 L19 10 Q20 10 20 11 L20 21 Q20 22 19 22 L5 22 Q4 22 4 21 L4 11 Q4 10 5 10 Z M10 10 L14 10 L14 7 A2 2 0 0 0 10 7 Z M11 14 L11 18 L13 18 L13 14 Z");
    private static readonly Geometry SignOut = Freeze("M3 2 L12 2 L12 5 L6 5 L6 19 L12 19 L12 22 L3 22 Z M15 6 L22 12 L15 18 L15 14 L9 14 L9 10 L15 10 Z");
    private static readonly Geometry Moon = Freeze("M14 2 A10 10 0 1 0 22 16 A9 9 0 0 1 14 2 Z");
    private static readonly Geometry Trash = Freeze("M8 2 L16 2 L17 5 L21 5 L21 8 L3 8 L3 5 L7 5 Z M5 10 L19 10 L18 21 Q18 22 16 22 L8 22 Q6 22 6 21 Z");

    internal static Geometry? Get(SpotlightSearchItem item) => item.Kind switch
    {
        SpotlightResultKind.Settings => Settings,
        SpotlightResultKind.SystemAction => SpotlightSystemCatalog.Find(item.Target)?.Key switch
        {
            "restart" or "advancedRestart" => Restart,
            "shutdown" => Power,
            "lock" => Lock,
            "signOut" => SignOut,
            "hibernate" => Moon,
            "recycleBin" => Trash,
            _ => null
        },
        _ => null
    };
}
