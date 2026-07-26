using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Models;

public enum SpotlightResultKind
{
    Application,
    File,
    Folder
}

public sealed record SpotlightSearchItem(
    string Id,
    SpotlightResultKind Kind,
    string Title,
    string Subtitle,
    string Target,
    string? IconPath = null)
{
    public double Score { get; init; }
    public ImageSource? Icon { get; init; }
    public string SectionTitle => Loc.Get(Kind == SpotlightResultKind.Application
        ? "spotlight.section.apps"
        : "spotlight.section.files");
    public string Glyph => Kind switch
    {
        SpotlightResultKind.Application => "\uE8A5",
        SpotlightResultKind.Folder => "\uE8B7",
        _ => "\uE8A5"
    };
}
