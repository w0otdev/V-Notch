using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Models;

public enum SpotlightResultKind
{
    Application,
    File,
    Folder,
    Calculation
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

    /// <summary>
    /// True for items republished from the launch history on an empty query;
    /// they group under a "Recents" header regardless of kind.
    /// </summary>
    public bool IsRecent { get; init; }

    public string SectionTitle => IsRecent
        ? Loc.Get("spotlight.section.recents")
        : Loc.Get(Kind switch
        {
            SpotlightResultKind.Application => "spotlight.section.apps",
            SpotlightResultKind.Calculation => "spotlight.section.calculator",
            _ => "spotlight.section.files"
        });

    public string Glyph => Kind switch
    {
        SpotlightResultKind.Application => "\uE71D",
        SpotlightResultKind.Folder => "\uE8B7",
        SpotlightResultKind.Calculation => "\uE8EF",
        _ => "\uE8A5"
    };
}
