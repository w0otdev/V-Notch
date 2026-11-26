using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ChangelogView : UserControl
{
    private readonly Dictionary<string, ChangelogEntry> _releases = new(StringComparer.Ordinal);
    private ChangelogEntry? _installedOnly;
    private IUpdateService? _service;
    private CancellationToken _lifetime;
    private int _nextPage;
    private int _loadGeneration;
    private bool _loadingOlder;
    private bool _loading;
    private int _selectedIndex = -1;
    private bool _animateWhenRowIsReady;
    private readonly SettingsSelectionMotion _selectionMotion;
    internal event Action<IReadOnlyList<UIElement>, IReadOnlyList<UIElement>?>? RevealRequested;

    public ChangelogView()
    {
        InitializeComponent();
        _selectionMotion = new SettingsSelectionMotion(SelectionMark, SelectionReveal, MarkScale, MarkOffset);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
            else
            {
                AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
                _selectionMotion.Settle();
            }
        };
    }

    private void OnReduceMotionChanged()
    {
        if (!AnimationConfig.ReduceMotion) return;
        _selectionMotion.Settle();
        RevealRequested?.Invoke(Array.Empty<UIElement>(), null);
    }

    internal async Task LoadAsync(IUpdateService updateService, Func<bool> canUseNetwork, CancellationToken lifetime = default)
    {
        if (_loading) return;
        _loading = true;
        _loadGeneration++;
        _service = updateService;
        _lifetime = lifetime;
        _nextPage = 0;
        _loadingOlder = false;
        try
        {
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
            VersionsText.Text = Loc.Get("changelog.versions");
            LoadingText.Text = Loc.Get("changelog.loading");
            OlderVersionsButton.Visibility = Visibility.Collapsed;
            OlderVersionsButton.IsEnabled = true;
            OlderVersionsButton.Content = Loc.Get("changelog.olderVersions");
            OlderVersionsButton.ToolTip = null;
            _releases.Clear();
            _installedOnly = null;
            _selectedIndex = -1;
            _animateWhenRowIsReady = false;
            SelectionMark.Visibility = Visibility.Collapsed;
            VersionList.Items.Clear();
            ReleaseHeader.Children.Clear();
            ReleaseNotes.Children.Clear();
            VersionScrollViewer.ScrollToTop();
            ContentScrollViewer.ScrollToTop();
            NotesSkeleton.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            ChangelogContent.Visibility = Visibility.Collapsed;

            int pageNumber = 1;
            var page = await updateService.GetChangelogPageAsync(pageNumber, lifetime);
            while (page.Releases.Count == 0 && page.HasMore)
                page = await updateService.GetChangelogPageAsync(++pageNumber, lifetime);
            if (lifetime.IsCancellationRequested) return;
            if (page.Releases.Count == 0)
            {
                ShowError(Loc.Get(canUseNetwork() ? "update.releaseUnavailable" : "update.localOnly"));
                return;
            }
            MergePage(page, pageNumber, updateService.CurrentVersion);
            VersionList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Settings closed while the release list was loading.
        }
        catch (Exception ex)
        {
            ShowError(canUseNetwork() ? Loc.Get("changelog.loadError", ex.Message) : Loc.Get("update.localOnly"));
        }
        finally
        {
            _loading = false;
            NotesSkeleton.Visibility = Visibility.Collapsed;
        }
    }

    private void VersionScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange > 0 && VersionScrollViewer.ScrollableHeight - e.VerticalOffset <= 120)
            LoadOlderVersionsAsync().SafeFireAndForget("CHANGELOG-LOAD");
    }

    private void OlderVersions_Click(object sender, RoutedEventArgs e)
        => LoadOlderVersionsAsync().SafeFireAndForget("CHANGELOG-LOAD");

    internal async Task LoadOlderVersionsAsync()
    {
        if (_service is not { } service || _nextPage == 0 || _loading || _loadingOlder || _lifetime.IsCancellationRequested) return;
        int generation = _loadGeneration;
        int pageNumber = _nextPage;
        var token = _lifetime;
        _loadingOlder = true;
        OlderVersionsButton.IsEnabled = false;
        OlderVersionsButton.Content = Loc.Get("changelog.loading");
        OlderVersionsButton.ToolTip = null;
        try
        {
            var page = await service.GetChangelogPageAsync(pageNumber, token);
            if (generation == _loadGeneration && !token.IsCancellationRequested)
                MergePage(page, pageNumber, service.CurrentVersion);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* Lifetime canceled on close. */ }
        catch (Exception ex)
        {
            if (generation == _loadGeneration)
                OlderVersionsButton.ToolTip = Loc.Get("changelog.loadError", ex.Message);
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                _loadingOlder = false;
                OlderVersionsButton.IsEnabled = true;
                OlderVersionsButton.Content = Loc.Get("changelog.olderVersions");
            }
        }
    }

    private void MergePage(ReleaseHistoryPage page, int pageNumber, string currentVersion)
    {
        foreach (var release in page.Releases)
        {
            var tag = release.ReleaseTag ?? release.Version;
            if (_releases.ContainsKey(tag)) continue;
            bool numbered = UpdateService.TryParseReleaseVersion(release.Version, out _, out var prerelease);
            _releases.Add(tag, new ChangelogEntry
            {
                Version = release.Version,
                Label = tag,
                Content = release.ReleaseNotes,
                Date = release.PublishedAt == default ? null : release.PublishedAt.ToLocalTime(),
                Numbered = numbered,
                Prerelease = release.IsPrerelease || (numbered && prerelease.Length != 0)
            });
        }

        var entries = _releases.Values.ToList();
        bool numberedCurrent = UpdateService.TryParseReleaseVersion(currentVersion, out _, out var currentPrerelease);
        var current = entries.Find(e => e.Version == currentVersion) ?? (numberedCurrent
            ? entries.Find(e => e.Numbered && UpdateService.CompareVersions(e.Version, currentVersion) == 0) : null);
        bool selectPublished = false;
        if (current == null)
        {
            current = _installedOnly ??= new ChangelogEntry
            {
                Version = currentVersion,
                Label = currentVersion,
                Numbered = numberedCurrent,
                Prerelease = numberedCurrent && currentPrerelease.Length != 0,
                Content = Loc.Get("changelog.currentInstalled")
            };
            entries.Add(current);
        }
        else if (_installedOnly?.Row is { } placeholder)
        {
            selectPublished = placeholder.IsSelected;
            VersionList.Items.Remove(placeholder);
            _installedOnly = null;
        }
        entries.Sort((a, b) =>
        {
            if (a.Numbered != b.Numbered) return a.Numbered ? -1 : 1;
            int order = a.Numbered ? UpdateService.CompareVersions(b.Version, a.Version) : 0;
            if (order == 0) order = Nullable.Compare(b.Date, a.Date);
            return order != 0 ? order : string.CompareOrdinal(a.Label, b.Label);
        });
        var latest = entries.Find(e => e != _installedOnly && e.Numbered && !e.Prerelease);
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool isCurrent = entry == current, isLatest = entry == latest;
            if (entry.Row != null && entry.IsCurrent == isCurrent && entry.IsLatest == isLatest) continue;
            entry.IsCurrent = isCurrent;
            entry.IsLatest = isLatest;
            if (entry.Row == null)
            {
                entry.Row = new ListBoxItem { Tag = entry.Version, DataContext = entry };
                entry.Row.Loaded += VersionRow_Loaded;
                UpdateRow(entry.Row, entry);
                VersionList.Items.Insert(i, entry.Row);
            }
            else
            {
                UpdateRow(entry.Row, entry);
                if (entry.Row.IsSelected) UpdateReleaseHeader(entry);
            }
        }
        if (selectPublished) VersionList.SelectedItem = current.Row;
        _selectedIndex = VersionList.SelectedIndex;
        _nextPage = page.HasMore ? pageNumber + 1 : 0;
        OlderVersionsButton.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void UpdateRow(ListBoxItem row, ChangelogEntry entry)
    {
        var labels = new StackPanel();
        var version = new TextBlock { Text = entry.Label, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        version.SetResourceReference(StyleProperty, entry.Prerelease ? "PrereleaseVersionLabel" : "StableVersionLabel");
        labels.Children.Add(version);
        var spoken = new List<string> { entry.Label };
        string? date = entry.Date?.ToString("d", Loc.GetCulture());
        if (date != null)
        {
            labels.Children.Add(new TextBlock { Text = date, FontSize = 11, Opacity = .77 });
            spoken.Add(date);
        }
        if (entry.IsLatest || entry.IsCurrent)
            labels.Children.Add(CreateStatusBadges(entry, compact: true, spoken));

        row.Content = labels;
        System.Windows.Automation.AutomationProperties.SetName(row, string.Join(", ", spoken));
    }

    private void VersionRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem { IsSelected: true } row)
            PositionSelectionMark(row, _animateWhenRowIsReady || _selectionMotion.IsMoving);
    }

    private void VersionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || VersionList.Items.Count == 0) return;
        int next = e.Key switch
        {
            Key.Up => VersionList.SelectedIndex - 1,
            Key.Down => VersionList.SelectedIndex + 1,
            Key.Home => 0,
            Key.End => VersionList.Items.Count - 1,
            _ => -1
        };
        if (next < 0 || next >= VersionList.Items.Count) return;
        VersionList.SelectedIndex = next;
        var row = (ListBoxItem)VersionList.SelectedItem;
        row.Focus();
        row.BringIntoView();
        e.Handled = true;
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not ListBoxItem row) return;
        int index = VersionList.SelectedIndex;
        int previous = _selectedIndex;
        _selectedIndex = index;
        if (previous >= 0)
            ReleaseWave.Capture(NotesViewport, Orientation.Vertical, forward: index > previous);
        // Changing the selected foreground can invalidate arrange on a sized row.
        // Its layout slot is still available; newly realized rows are handled on Loaded.
        PositionSelectionMark(row, animate: previous >= 0);
        DisplayChangelog((ChangelogEntry)row.DataContext);
        if (IsVisible && AnimationConfig.ClientAreaAnimation && !AnimationConfig.ReduceMotion)
            RevealRelease(includeVersionRows: previous < 0);
    }

    private void RevealRelease(bool includeVersionRows)
    {
        var notes = new List<UIElement> { ReleaseHeader };
        notes.AddRange(ReleaseNotes.Children.Cast<UIElement>());
        List<UIElement>? versions = null;
        if (includeVersionRows)
        {
            versions = new() { VersionsText };
            versions.AddRange(VersionList.Items.Cast<ListBoxItem>());
            if (OlderVersionsButton.Visibility == Visibility.Visible) versions.Add(OlderVersionsButton);
        }
        RevealRequested?.Invoke(notes, versions);
    }

    private void VersionList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (VersionList.SelectedItem is ListBoxItem row && row.IsArrangeValid)
            PositionSelectionMark(row, _animateWhenRowIsReady || _selectionMotion.IsMoving);
    }

    private void PositionSelectionMark(ListBoxItem row, bool animate)
    {
        if (row.ActualHeight <= 0 || VisualTreeHelper.GetParent(row) is not UIElement host)
        {
            _animateWhenRowIsReady = animate;
            return;
        }
        _animateWhenRowIsReady = false;
        // The reveal layer already follows the row's animated translation.
        // Aim the spring at the layout slot so that translation is applied once.
        var origin = new Point(0, 1) + VisualTreeHelper.GetOffset(row);
        double target = host.TranslatePoint(origin, VersionList).Y;
        double height = Math.Max(24, row.ActualHeight - 2);
        SelectionMark.Visibility = Visibility.Visible;
        _selectionMotion.MoveTo(target, height, animate);
    }

    private void DisplayChangelog(ChangelogEntry entry)
    {
        ChangelogContent.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        UpdateReleaseHeader(entry);
        ReleaseNotes.Children.Clear();
        RenderMarkdownContent(ReleaseNotes, entry.Content);
        ContentScrollViewer.ScrollToTop();
    }

    private void UpdateReleaseHeader(ChangelogEntry entry)
    {
        ReleaseHeader.Children.Clear();
        var versionTitle = new TextBlock
        {
            Text = Loc.Get("changelog.version", entry.Version),
            FontSize = 24,
            FontWeight = FontWeights.Bold
        };
        versionTitle.SetResourceReference(TextBlock.ForegroundProperty, entry.Prerelease ? "PrereleaseVersionBrush" : "StableVersionBrush");
        ReleaseHeader.Children.Add(versionTitle);

        if (entry.Date is { } date) ReleaseHeader.Children.Add(new TextBlock
        {
            Text = date.ToString("D", Loc.GetCulture()),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
            Margin = new Thickness(0, 4, 0, 0)
        });

        if (entry.IsLatest || entry.IsCurrent)
            ReleaseHeader.Children.Add(CreateStatusBadges(entry, compact: false));
    }

    private static WrapPanel CreateStatusBadges(ChangelogEntry entry, bool compact, List<string>? spoken = null)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, compact ? 2 : 8, 0, 0) };
        if (entry.IsLatest) AddBadge("changelog.latest");
        if (entry.IsCurrent) AddBadge("changelog.current");
        return panel;

        void AddBadge(string labelKey)
        {
            string text = Loc.Get(labelKey);
            spoken?.Add(text);
            var label = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
            if (compact)
                label.SetResourceReference(StyleProperty, entry.Prerelease ? "PrereleaseVersionLabel" : "StableVersionLabel");
            else
                label.SetResourceReference(TextBlock.ForegroundProperty, entry.Prerelease ? "PrereleaseVersionBrush" : "StableVersionBrush");
            var badge = new Border
            {
                Child = label,
                CornerRadius = new CornerRadius(8),
                Padding = compact ? new Thickness(4, 1, 4, 1) : new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, compact ? 3 : 6, 0)
            };
            badge.BorderThickness = new Thickness(1);
            badge.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(TextBlock.Foreground)) { Source = label });
            panel.Children.Add(badge);
        }
    }

    private static readonly MarkdownPanelOptions ChangelogMarkdownOptions = CreateChangelogMarkdownOptions();

    private static MarkdownPanelOptions CreateChangelogMarkdownOptions()
    {
        static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        return new MarkdownPanelOptions
        {
            BodyFont = SetupFonts.SFProDisplayFont,
            InlineCodeFont = SetupFonts.SFProDisplayFont,
            CodeBlockFont = SetupFonts.SFProDisplayFont,
            CodeBlockFontWeight = FontWeights.Bold,
            BodyFontSize = 13,
            BodyLineHeight = 20,
            BodyFontWeight = FontWeights.Bold,
            ResolveHeadingStyle = static level => (
                level switch { 1 => 20, 2 => 18, 3 => 16, _ => 14.5 }, FontWeights.Bold, new Thickness(0, 12, 0, 8)),
            ParagraphMargin = new Thickness(0, 4, 0, 4),
            SoftBreakAsSpace = true,
            LinkImages = true,
            BoldForeground = UiPalette.PrimaryBrush,
            BodyForeground = Frozen(Color.FromRgb(204, 204, 204)),
            HeadingForeground = UiPalette.PrimaryBrush,
            ListTextForeground = Frozen(Color.FromRgb(224, 224, 224)),
            BulletForeground = Frozen(Color.FromRgb(0, 102, 255)),
            OrderedMarkerForeground = Frozen(Color.FromRgb(136, 136, 136)),
            CheckedTaskForeground = Frozen(Color.FromRgb(0, 204, 102)),
            UncheckedTaskForeground = Frozen(Color.FromRgb(136, 136, 136)),
            ListItemMargin = new Thickness(0, 4, 0, 4),
            QuoteBorderBrush = Frozen(Color.FromRgb(64, 64, 64)),
            QuoteForeground = Frozen(Color.FromRgb(170, 170, 170)),
            QuotePadding = new Thickness(10, 2, 0, 2),
            QuoteMargin = new Thickness(0, 4, 0, 4),
            ThematicBreakBrush = Frozen(Color.FromRgb(42, 42, 42)),
            ThematicBreakMargin = new Thickness(0, 8, 0, 8),
            InlineCodeForeground = Frozen(Color.FromRgb(230, 230, 230)),
            InlineCodeBackground = Frozen(Color.FromArgb(20, 255, 255, 255)),
            TableHeaderForeground = UiPalette.PrimaryBrush,
            TableCellForeground = Frozen(Color.FromRgb(214, 214, 214)),
            LinkForeground = Frozen(Color.FromRgb(0, 102, 255)),
            UnderlineLinks = false,
            HttpsOnlyLinks = false
        };
    }

    internal static void RenderMarkdownContent(StackPanel targetPanel, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            var emptyText = new TextBlock
            {
                Text = Loc.Get("changelog.noNotes"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontStyle = FontStyles.Italic
            };
            targetPanel.Children.Add(emptyText);
            return;
        }

        MarkdownWpfRenderer.AppendToPanel(targetPanel, content, ChangelogMarkdownOptions);
    }

    internal static void PopulateInlineMarkdown(TextBlock textBlock, string text) =>
        MarkdownWpfRenderer.PopulateInlines(textBlock.Inlines, text, ChangelogMarkdownOptions, clearFirst: true);

    private void ShowError(string message)
    {
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        ChangelogContent.Visibility = Visibility.Collapsed;
    }

    private sealed class ChangelogEntry
    {
        public ListBoxItem? Row { get; set; }
        public string Version { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
        public bool Numbered { get; set; }
        public bool Prerelease { get; set; }
        public string Content { get; set; } = string.Empty;
        public bool IsLatest { get; set; }
        public bool IsCurrent { get; set; }
    }
}
