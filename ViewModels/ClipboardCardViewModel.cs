using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VNotch.Controls;
using VNotch.Models;

namespace VNotch.ViewModels;

public sealed partial class ClipboardCardViewModel : ObservableObject
{
    private static readonly FontFamily SharedContentFont = new("pack://application:,,,/V-Notch;component/Fonts/#SF Pro Display, Segoe UI");
    private static readonly Brush SharedCardBrush = CreateSurfaceBrush();
    private Brush? _cardBrush;
    private string _age = "";
    public ClipboardEntry Entry { get; private set; }

    public void UpdateEntry(ClipboardEntry entry)
    {
        if (entry.Id != Entry.Id) throw new ArgumentException("Cannot change a card's identity.", nameof(entry));
        if (Entry == entry) return;
        Entry = entry;
        _cardBrush = null;
        RefreshAge(DateTime.UtcNow);
        OnPropertyChanged(string.Empty);
    }
    public string Preview => Entry.Kind == ClipboardKind.Image ? "" : Entry.Preview;
    public string Title => Entry.Kind == ClipboardKind.Image && Entry.Title is "Image" or "Screenshot"
        ? Services.Loc.Get(Entry.Title == "Image" ? "clipboard.image" : "clipboard.screenshot") : Entry.Title;
    public string Source => string.IsNullOrEmpty(Entry.SourceApp) ? Services.Loc.Get("clipboard.source") : Entry.SourceApp;
    public string Size => Entry.ByteSize < 1024 ? Services.Loc.Get("clipboard.bytes", Entry.ByteSize) : Entry.ByteSize < 1024 * 1024
        ? (Entry.ByteSize / 1024d).ToString("0.#", Services.Loc.GetCulture()) + " KB"
        : (Entry.ByteSize / (1024d * 1024)).ToString("0.#", Services.Loc.GetCulture()) + " MB";
    public string Age => _age;

    internal void RefreshAge(DateTime nowUtc)
    {
        var age = nowUtc - Entry.CopiedUtc;
        string label = age.TotalMinutes < 1 ? Services.Loc.Get("clipboard.justNow")
            : age.TotalHours < 1 ? Services.Loc.Get("clipboard.minutesAgo", (int)age.TotalMinutes)
            : age.TotalDays < 1 ? Services.Loc.Get("clipboard.hoursAgo", (int)age.TotalHours)
            : Services.Loc.Get("clipboard.daysAgo", (int)age.TotalDays);
        SetProperty(ref _age, label, nameof(Age));
    }
    public string FileExtension
    {
        get
        {
            if (Entry.Kind != ClipboardKind.File && Entry.Kind != ClipboardKind.Code) return string.Empty;
            return Entry.FileCount == 1 ? Entry.PrimaryExtension.ToLowerInvariant() : string.Empty;
        }
    }

    public string FileTypeTag
    {
        get
        {
            if (Entry.FileCount > 1) return Services.Loc.Get("clipboard.fileCount", Entry.FileCount);
            string ext = FileExtension;
            if (string.IsNullOrEmpty(ext)) return Entry.Kind == ClipboardKind.Code ? "CODE" : "FILE";
            string clean = ext.TrimStart('.');
            return clean.Length > 5 ? clean[..5].ToUpperInvariant() : clean.ToUpperInvariant();
        }
    }

    public Brush FileAccentBrush => GetFileAccentBrush(FileExtension, Entry.Kind);
    public Brush FileBadgeBackgroundBrush => GetFileBadgeBackgroundBrush(FileExtension, Entry.Kind);

    private static Brush GetFileAccentBrush(string ext, ClipboardKind kind)
    {
        if (kind == ClipboardKind.Code) return CreateFrozenBrush(Color.FromRgb(0x64, 0xD2, 0xFF));
        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => CreateFrozenBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
            ".pdf" => CreateFrozenBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
            ".doc" or ".docx" or ".rtf" or ".odt" or ".txt" or ".md" => CreateFrozenBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
            ".xls" or ".xlsx" or ".csv" or ".tsv" => CreateFrozenBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
            ".ppt" or ".pptx" or ".key" => CreateFrozenBrush(Color.FromRgb(0xFF, 0x69, 0x61)),
            ".cs" or ".js" or ".ts" or ".py" or ".cpp" or ".c" or ".h" or ".json" or ".xml" or ".html" or ".css" or ".sql" => CreateFrozenBrush(Color.FromRgb(0x64, 0xD2, 0xFF)),
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => CreateFrozenBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)),
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" => CreateFrozenBrush(Color.FromRgb(0x5E, 0x5C, 0xE6)),
            _ => CreateFrozenBrush(Color.FromRgb(0x8E, 0x8E, 0x93))
        };
    }

    private static Brush GetFileBadgeBackgroundBrush(string ext, ClipboardKind kind)
    {
        if (kind == ClipboardKind.Code) return CreateFrozenBrush(Color.FromArgb(0x28, 0x64, 0xD2, 0xFF));
        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0x9F, 0x0A)),
            ".pdf" => CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0x45, 0x3A)),
            ".doc" or ".docx" or ".rtf" or ".odt" or ".txt" or ".md" => CreateFrozenBrush(Color.FromArgb(0x28, 0x0A, 0x84, 0xFF)),
            ".xls" or ".xlsx" or ".csv" or ".tsv" => CreateFrozenBrush(Color.FromArgb(0x28, 0x30, 0xD1, 0x58)),
            ".ppt" or ".pptx" or ".key" => CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0x69, 0x61)),
            ".cs" or ".js" or ".ts" or ".py" or ".cpp" or ".c" or ".h" or ".json" or ".xml" or ".html" or ".css" or ".sql" => CreateFrozenBrush(Color.FromArgb(0x28, 0x64, 0xD2, 0xFF)),
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => CreateFrozenBrush(Color.FromArgb(0x28, 0xBF, 0x5A, 0xF2)),
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" => CreateFrozenBrush(Color.FromArgb(0x28, 0x5E, 0x5C, 0xE6)),
            _ => CreateFrozenBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF))
        };
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public string Glyph => Entry.Kind switch
    {
        ClipboardKind.Image => "\uEB9F",
        ClipboardKind.Link => "\uE71B",
        ClipboardKind.File => FileExtension switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uF012",
            ".pdf" => "\uEA90",
            ".mp3" or ".wav" or ".flac" or ".m4a" => "\uEC4F",
            ".mp4" or ".mkv" or ".mov" or ".avi" => "\uE714",
            _ => "\uE8A5"
        },
        ClipboardKind.Code => "\uE943",
        ClipboardKind.Color => "\uE790",
        _ => "\uE8A4"
    };
    public TrayIconKind IconKind => Entry.Kind switch
    {
        ClipboardKind.Image => TrayIconKind.Images,
        ClipboardKind.Link => TrayIconKind.Links,
        ClipboardKind.File => TrayIconKind.Files,
        ClipboardKind.Color => TrayIconKind.Colors,
        ClipboardKind.Code => TrayIconKind.Code,
        _ => TrayIconKind.Text
    };
    public string MoreLabel => Services.Loc.Get("clipboard.more");
    public string DetailsLabel => Services.Loc.Get("clipboard.details");
    public string PinLabel => Services.Loc.Get(Entry.IsPinned ? "clipboard.unpin" : "clipboard.pin");
    public string GroupsLabel => Services.Loc.Get("clipboard.groups");
    public string PersonalLabel => Services.Loc.Get(Entry.IsPersonal ? "clipboard.moveOut" : "clipboard.movePersonal");
    public string ArchiveLabel => Services.Loc.Get(Entry.IsArchived ? "clipboard.unarchive" : "clipboard.archive");
    public string DeleteLabel => Services.Loc.Get("clipboard.delete");

    public void ApplyLocalization()
    {
        RefreshAge(DateTime.UtcNow);
        foreach (string property in new[] { nameof(MoreLabel), nameof(DetailsLabel), nameof(PinLabel), nameof(GroupsLabel),
            nameof(PersonalLabel), nameof(ArchiveLabel), nameof(DeleteLabel), nameof(CopiedLabel), nameof(Title), nameof(Source), nameof(Size), nameof(FileTypeTag) })
            OnPropertyChanged(property);
    }
    public FontFamily ContentFont => SharedContentFont;
    public Brush CardBrush => _cardBrush ??= CreateCardBrush(Entry);
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private ImageSource? _sourceIcon;
    [ObservableProperty] private ImageSource? _fileIcon;
    public string CopiedLabel => Services.Loc.Get("clipboard.copied");
    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private bool _isMenuOpen;
    public ClipboardCardViewModel(ClipboardEntry entry)
    {
        Entry = entry;
        RefreshAge(DateTime.UtcNow);
    }

    private static Brush CreateCardBrush(ClipboardEntry entry)
    {
        if (entry.Kind != ClipboardKind.Color || !Services.Clipboard.ClipboardClassifier.TryParseColor(entry.Preview, out uint argb))
            return SharedCardBrush;
        var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }

    private static Brush CreateSurfaceBrush()
    {
        var brush = new SolidColorBrush(ClipboardTrayTokens.SurfaceElevated);
        brush.Freeze();
        return brush;
    }
}
