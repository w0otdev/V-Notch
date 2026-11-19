using System.IO;
using System.Windows.Media;
using VNotch.Models;

namespace VNotch.Services.Spotlight;

internal readonly struct SpotlightResultPresentation(SpotlightSearchItem item)
{
    public string DisplayTitle
    {
        get
        {
            var entry = SpotlightSystemCatalog.Find(item.Target);
            if (entry?.Kind == item.Kind) return ResolveSystemLabel($"spotlight.system.{entry.Key}", entry.Keywords);
            if (item.Kind is not (SpotlightResultKind.Application or SpotlightResultKind.File)) return item.Title;
            string extension = Path.GetExtension(item.Target);
            return extension.Length > 0 && item.Title.Length > extension.Length
                && item.Title.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? item.Title[..^extension.Length] : item.Title;
        }
    }

    public string DisplaySubtitle
    {
        get
        {
            var entry = SpotlightSystemCatalog.Find(item.Target);
            if (entry?.Kind == item.Kind)
            {
                return entry.Kind switch
                {
                    SpotlightResultKind.Settings => ResolveSystemLabel("spotlight.system.settings", "Windows Settings"),
                    SpotlightResultKind.SystemAction => ResolveSystemLabel("spotlight.system.actions", "System actions"),
                    _ => ResolveSystemLabel("spotlight.system.tools", "Windows tools")
                };
            }
            if (!Path.IsPathFullyQualified(item.Subtitle)) return item.Subtitle;
            return item.Kind == SpotlightResultKind.Application ? Loc.Get("spotlight.kind.application")
                : Path.GetDirectoryName(item.Target) ?? item.Subtitle;
        }
    }

    private static string ResolveSystemLabel(string key, string fallbackKeywords)
    {
        string text = Loc.Get(key);
        if (!string.IsNullOrWhiteSpace(text) && text != key) return text;
        int sep = fallbackKeywords.IndexOf(';');
        return sep >= 0 ? fallbackKeywords[..sep] : fallbackKeywords;
    }

    public string SectionTitle
    {
        get
        {
            if (item.IsPinned) return Loc.Get("spotlight.section.pinned");
            return item.IsRecent
                ? Loc.Get("spotlight.section.recents")
                : Loc.Get(item.Kind switch
                {
                    SpotlightResultKind.Application => "spotlight.section.apps",
                    SpotlightResultKind.Calculation => "spotlight.section.calculator",
                    SpotlightResultKind.Settings => "spotlight.system.settings",
                    SpotlightResultKind.SystemAction => "spotlight.system.actions",
                    _ => "spotlight.section.files"
                });
        }
    }

    public Geometry? SolidIcon => SpotlightSolidIcons.Get(item);

    public string Glyph => SpotlightSystemCatalog.Find(item.Target)?.Glyph ?? (item.Kind switch
    {
        SpotlightResultKind.Application => "\uE71D",
        SpotlightResultKind.Folder => "\uE8B7",
        SpotlightResultKind.Calculation => "\uE8EF",
        _ => "\uE8A5"
    });
}
