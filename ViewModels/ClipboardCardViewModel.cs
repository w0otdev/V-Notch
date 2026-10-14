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
    private bool? _isMasked;
    private string _age = "";
    public ClipboardEntry Entry { get; private set; }

    public void UpdateEntry(ClipboardEntry entry)
        => UpdatePreparedEntry(entry, null);

    internal bool? GetCachedMasking(ClipboardEntry entry) =>
        entry.IsSensitive == Entry.IsSensitive && entry.Preview == Entry.Preview &&
        entry.SourceApp == Entry.SourceApp && entry.SourceExecutable == Entry.SourceExecutable ? _isMasked : null;

    internal void UpdatePreparedEntry(ClipboardEntry entry, bool? isMasked)
    {
        if (entry.Id != Entry.Id) throw new ArgumentException("Cannot change a card's identity.", nameof(entry));
        if (Entry == entry)
        {
            if (isMasked.HasValue) _isMasked = isMasked;
            return;
        }
        Hide();
        ReleaseMaskedPreviewSize();
        _isMasked = isMasked ?? GetCachedMasking(entry);
        Entry = entry;
        _cardBrush = null;
        RefreshAge(DateTime.UtcNow);
        OnPropertyChanged(string.Empty);
    }
    private bool _isRevealed;
    private string? _revealedText;
    private int _revealVersion;
    private int _copyVersion;

    internal int NextCopyVersion() => ++_copyVersion;
    internal bool IsCopyVersionCurrent(int version) => version == _copyVersion;

    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (SetProperty(ref _isRevealed, value))
            {
                OnPropertyChanged(nameof(MaskedPreviewWidth));
                OnPropertyChanged(nameof(MaskedPreviewHeight));
                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(DisplayPreview));
                OnPropertyChanged(nameof(BlurRadius));
                OnPropertyChanged(nameof(RevealIconKind));
                OnPropertyChanged(nameof(RevealTooltip));
            }
        }
    }

    public double BlurRadius => IsMasked && !IsRevealed ? 12.0 : 0.0;

    public bool IsMasked => _isMasked ??= Entry.IsSensitive || Services.Clipboard.ClipboardSensitiveData.IsSensitive(Entry.Preview, Entry.SourceApp, Entry.SourceExecutable);

    public string Preview => IsMasked && !IsRevealed
        ? Services.Clipboard.ClipboardSensitiveData.Mask
        : !string.IsNullOrEmpty(_revealedText)
            ? _revealedText
            : Entry.Kind == ClipboardKind.Image ? "" : Entry.Preview;

    public string DisplayPreview => Preview;

    private System.Windows.Size? _maskedPreviewSize;
    internal bool NeedsMaskedPreviewSize => IsMasked && _maskedPreviewSize == null && Entry.Kind is not (ClipboardKind.File or ClipboardKind.Image);
    public double MaskedPreviewWidth => IsMasked && !IsRevealed ? _maskedPreviewSize?.Width ?? double.NaN : double.NaN;
    public double MaskedPreviewHeight => IsMasked && !IsRevealed ? _maskedPreviewSize?.Height ?? double.NaN : double.NaN;

    internal async Task PrepareMaskedPreviewAsync(Services.Clipboard.ClipboardHistoryStore store, CancellationToken cancellation)
    {
        if (!NeedsMaskedPreviewSize) return;
        int version = _revealVersion;
        var content = await store.GetContentAsync(Entry.Id);
        if (cancellation.IsCancellationRequested || version != _revealVersion || Entry.IsPersonal && !store.IsPersonalUnlocked) return;
        // Measure off-tree; only dimensions survive, never secret characters or a layout mask.
        var text = new System.Windows.Controls.TextBlock
        {
            Text = content.Text,
            FontFamily = SharedContentFont,
            FontSize = 14,
            FontWeight = System.Windows.FontWeights.SemiBold,
            LineHeight = 19,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            MaxHeight = 96
        };
        text.Measure(new System.Windows.Size(182, 96));
        _maskedPreviewSize = text.DesiredSize;
        text.Text = "";
        OnPropertyChanged(nameof(MaskedPreviewWidth));
        OnPropertyChanged(nameof(MaskedPreviewHeight));
    }

    internal void ReleaseMaskedPreviewSize()
    {
        _maskedPreviewSize = null;
        OnPropertyChanged(nameof(MaskedPreviewWidth));
        OnPropertyChanged(nameof(MaskedPreviewHeight));
    }

    public System.Windows.Visibility MaskActionVisibility => IsMasked ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public TrayIconKind RevealIconKind => IsRevealed ? TrayIconKind.Lock : TrayIconKind.Unlock;

    public string RevealTooltip => IsRevealed ? Services.Loc.Get("clipboard.hidePassword") : Services.Loc.Get("clipboard.revealPassword");

    public async Task RevealAsync(Services.Clipboard.ClipboardHistoryStore store)
    {
        if (!IsMasked) return;
        int version = ++_revealVersion;
        if (string.IsNullOrEmpty(_revealedText))
        {
            var content = await store.GetContentAsync(Entry.Id);
            if (version != _revealVersion || Entry.IsPersonal && !store.IsPersonalUnlocked) return;
            string resolved = Entry.Kind == ClipboardKind.File || content.Text.Length == 0
                ? string.Join(Environment.NewLine, content.Files.Select(file => file.Name)) : content.Text;
            if (!string.IsNullOrEmpty(resolved))
                _revealedText = resolved;
        }
        IsRevealed = true;
    }

    public void Hide()
    {
        ++_revealVersion;
        _revealedText = null;
        IsRevealed = false;
    }

    public string Title => IsMasked && !IsRevealed ? Services.Clipboard.ClipboardSensitiveData.Mask : Entry.Kind == ClipboardKind.Image && Entry.Title is "Image" or "Screenshot"
        ? Services.Loc.Get(Entry.Title == "Image" ? "clipboard.image" : "clipboard.screenshot") : !string.IsNullOrEmpty(_revealedText) ? _revealedText : Entry.Title;
    public string Source => string.IsNullOrEmpty(Entry.SourceApp) ? Services.Loc.Get("clipboard.source") : Entry.SourceApp;
    public string FooterSource => Entry.IsPersonal ? Services.Loc.Get("clipboard.category.personal") : Source;
    internal ImageSource? PreferredSourceIcon => TrayAppIcons.FindSource(Entry.SourceApp, Entry.SourceExecutable);
    internal ImageSource? PreferredFileIcon => TrayAppIcons.FindFile(FileExtension);
    public ImageSource DisplaySourceIcon => PreferredSourceIcon ?? SourceIcon ?? TrayAppIcons.ForContent(Entry.Kind);
    public ImageSource FilePreviewIcon => PreferredFileIcon ?? FileIcon ?? TrayAppIcons.Get(TrayAppIconKind.Document);
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

    private static readonly Brush AccentCodeBrush = CreateFrozenBrush(Color.FromRgb(0x64, 0xD2, 0xFF));
    private static readonly Brush AccentArchiveBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush AccentPdfBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush AccentDocumentBrush = CreateFrozenBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush AccentSpreadsheetBrush = CreateFrozenBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush AccentPresentationBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0x69, 0x61));
    private static readonly Brush AccentAudioBrush = CreateFrozenBrush(Color.FromRgb(0xBF, 0x5A, 0xF2));
    private static readonly Brush AccentVideoBrush = CreateFrozenBrush(Color.FromRgb(0x5E, 0x5C, 0xE6));
    private static readonly Brush AccentDefaultBrush = CreateFrozenBrush(Color.FromRgb(0x8E, 0x8E, 0x93));

    private static readonly Brush BadgeCodeBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0x64, 0xD2, 0xFF));
    private static readonly Brush BadgeArchiveBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0x9F, 0x0A));
    private static readonly Brush BadgePdfBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0x45, 0x3A));
    private static readonly Brush BadgeDocumentBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0x0A, 0x84, 0xFF));
    private static readonly Brush BadgeSpreadsheetBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0x30, 0xD1, 0x58));
    private static readonly Brush BadgePresentationBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0xFF, 0x69, 0x61));
    private static readonly Brush BadgeAudioBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0xBF, 0x5A, 0xF2));
    private static readonly Brush BadgeVideoBrush = CreateFrozenBrush(Color.FromArgb(0x28, 0x5E, 0x5C, 0xE6));
    private static readonly Brush BadgeDefaultBrush = CreateFrozenBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));

    public Brush FileAccentBrush => GetFileAccentBrush(FileExtension, Entry.Kind);
    public Brush FileBadgeBackgroundBrush => GetFileBadgeBackgroundBrush(FileExtension, Entry.Kind);

    private static Brush GetFileAccentBrush(string ext, ClipboardKind kind)
    {
        if (kind == ClipboardKind.Code) return AccentCodeBrush;
        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => AccentArchiveBrush,
            ".pdf" => AccentPdfBrush,
            ".doc" or ".docx" or ".rtf" or ".odt" or ".txt" or ".md" => AccentDocumentBrush,
            ".xls" or ".xlsx" or ".csv" or ".tsv" => AccentSpreadsheetBrush,
            ".ppt" or ".pptx" or ".key" => AccentPresentationBrush,
            ".cs" or ".js" or ".ts" or ".py" or ".cpp" or ".c" or ".h" or ".json" or ".xml" or ".html" or ".css" or ".sql" => AccentCodeBrush,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => AccentAudioBrush,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" => AccentVideoBrush,
            _ => AccentDefaultBrush
        };
    }

    private static Brush GetFileBadgeBackgroundBrush(string ext, ClipboardKind kind)
    {
        if (kind == ClipboardKind.Code) return BadgeCodeBrush;
        return ext switch
        {
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" => BadgeArchiveBrush,
            ".pdf" => BadgePdfBrush,
            ".doc" or ".docx" or ".rtf" or ".odt" or ".txt" or ".md" => BadgeDocumentBrush,
            ".xls" or ".xlsx" or ".csv" or ".tsv" => BadgeSpreadsheetBrush,
            ".ppt" or ".pptx" or ".key" => BadgePresentationBrush,
            ".cs" or ".js" or ".ts" or ".py" or ".cpp" or ".c" or ".h" or ".json" or ".xml" or ".html" or ".css" or ".sql" => BadgeCodeBrush,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => BadgeAudioBrush,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" => BadgeVideoBrush,
            _ => BadgeDefaultBrush
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
            nameof(PersonalLabel), nameof(ArchiveLabel), nameof(DeleteLabel), nameof(CopiedLabel), nameof(Title), nameof(Source), nameof(FooterSource), nameof(Size), nameof(FileTypeTag) })
            OnPropertyChanged(property);
    }
    public FontFamily ContentFont => SharedContentFont;
    public Brush CardBrush => _cardBrush ??= CreateCardBrush(Entry, IsMasked);
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplaySourceIcon))]
    private ImageSource? _sourceIcon;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePreviewIcon))]
    private ImageSource? _fileIcon;
    public string CopiedLabel => Services.Loc.Get("clipboard.copied");
    [ObservableProperty] private bool _isCopied;
    [ObservableProperty] private bool _isMenuOpen;
    public ClipboardCardViewModel(ClipboardEntry entry)
        : this(entry, null)
    {
    }

    internal ClipboardCardViewModel(ClipboardEntry entry, bool? isMasked)
    {
        Entry = entry;
        _isMasked = isMasked;
        RefreshAge(DateTime.UtcNow);
    }

    private static Brush CreateCardBrush(ClipboardEntry entry, bool isMasked)
    {
        if (isMasked || entry.Kind != ClipboardKind.Color || !Services.Clipboard.ClipboardClassifier.TryParseColor(entry.Preview, out uint argb))
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
