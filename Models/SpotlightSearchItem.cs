using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Models;

public enum SpotlightResultKind
{
    Application,
    File,
    Folder,
    Calculation,
    Settings,
    SystemAction
}

public sealed record SpotlightSearchItem(
    string Id,
    SpotlightResultKind Kind,
    string Title,
    string Subtitle,
    string Target,
    string? IconPath = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Equals(SpotlightSearchItem? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public double Score { get; init; }

    // Keep the real title/target for matching, launching and copying paths.
    public string DisplayTitle
    {
        get
        {
            var entry = Services.Spotlight.SpotlightSystemCatalog.Find(Target);
            if (entry?.Kind == Kind) return entry.CreateItem().Title;
            if (Kind is not (SpotlightResultKind.Application or SpotlightResultKind.File)) return Title;
            string extension = Path.GetExtension(Target);
            return extension.Length > 0 && Title.Length > extension.Length
                && Title.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? Title[..^extension.Length] : Title;
        }
    }

    public string DisplaySubtitle
    {
        get
        {
            var entry = Services.Spotlight.SpotlightSystemCatalog.Find(Target);
            if (entry?.Kind == Kind) return entry.CreateItem().Subtitle;
            if (!Path.IsPathFullyQualified(Subtitle)) return Subtitle;
            return Kind == SpotlightResultKind.Application ? Loc.Get("spotlight.kind.application")
                : Path.GetDirectoryName(Target) ?? Subtitle;
        }
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public bool IsRecent { get; init; }
    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SectionTitle)));
        }
    }

    public string SectionTitle => IsPinned ? Loc.Get("spotlight.section.pinned") : IsRecent
        ? Loc.Get("spotlight.section.recents")
        : Loc.Get(Kind switch
        {
            SpotlightResultKind.Application => "spotlight.section.apps",
            SpotlightResultKind.Calculation => "spotlight.section.calculator",
            SpotlightResultKind.Settings => "spotlight.system.settings",
            SpotlightResultKind.SystemAction => "spotlight.system.actions",
            _ => "spotlight.section.files"
        });

    public Geometry? SolidIcon => Services.Spotlight.SpotlightSolidIcons.Get(this);

    public bool IsShortcut => Kind == SpotlightResultKind.Application
        && string.Equals(Path.GetExtension(Target), ".lnk", StringComparison.OrdinalIgnoreCase);

    public string Glyph => Services.Spotlight.SpotlightSystemCatalog.Find(Target)?.Glyph ?? (Kind switch
    {
        SpotlightResultKind.Application => "\uE71D",
        SpotlightResultKind.Folder => "\uE8B7",
        SpotlightResultKind.Calculation => "\uE8EF",
        _ => "\uE8A5"
    });
}
