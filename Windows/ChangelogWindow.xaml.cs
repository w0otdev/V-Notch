using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Controls;
using VNotch.Services;

namespace VNotch.Windows;

public partial class ChangelogWindow : Window
{
    private readonly IUpdateService _updateService;
    private List<ChangelogEntry> _changelogEntries = new();

    public ChangelogWindow(IUpdateService updateService)
    {
        InitializeComponent();
        AnimationPrimitives.ApplyFpsToTree(this);
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));

        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        Title = Loc.Get("changelog.windowTitle");
        TitleText.Text = Loc.Get("changelog.title");
        SubtitleText.Text = Loc.Get("changelog.subtitle");
        VersionsText.Text = Loc.Get("changelog.versions");
        LoadingText.Text = Loc.Get("changelog.loading");
        ErrorText.Text = Loc.Get("changelog.loadFailed");
        TooltipHelper.SetLocalizedTooltip(CloseChangelogButton, "tooltip.close");

        Loaded += ChangelogWindow_Loaded;
    }

    private void PlayEntranceAnimation()
    {
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(400);

        var opacityAnim = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        var scaleAnim = new DoubleAnimation(0.95, 1, duration) { EasingFunction = ease };
        var translateAnim = new DoubleAnimation(20, 0, duration) { EasingFunction = ease };

        MainShell.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    private async void ChangelogWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PlayEntranceAnimation();
        await LoadChangelog();
    }

    private async System.Threading.Tasks.Task LoadChangelog()
    {
        try
        {
            LoadingText.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            ChangelogContent.Visibility = Visibility.Collapsed;

            // Try to get all releases
            var allReleases = await _updateService.GetAllReleasesAsync();
            if (allReleases.Count == 0)
            {
                ShowError(Loc.Get("changelog.loadFailed"));
                return;
            }
            var currentVersion = _updateService.CurrentVersion;

            bool currentFound = false;
            foreach (var release in allReleases)
            {
                _changelogEntries.Add(new ChangelogEntry
                {
                    Version = release.Version,
                    Date = release.PublishedAt,
                    Content = release.ReleaseNotes,
                    IsLatest = release.Version == allReleases.FirstOrDefault()?.Version,
                    IsCurrent = release.Version == currentVersion
                });

                if (release.Version == currentVersion)
                    currentFound = true;
            }

            // Add current version if different and not found in releases
            if (!currentFound)
            {
                _changelogEntries.Add(new ChangelogEntry
                {
                    Version = currentVersion,
                    Date = DateTime.UtcNow,
                    Content = Loc.Get("changelog.currentInstalled"),
                    IsCurrent = true
                });
            }

            // Sort by version (descending, including pre-release versions)
            _changelogEntries.Sort((a, b) =>
            {
                bool aValid = UpdateService.TryParseReleaseVersion(a.Version, out _, out _);
                bool bValid = UpdateService.TryParseReleaseVersion(b.Version, out _, out _);
                if (aValid && bValid) return UpdateService.CompareVersions(b.Version, a.Version);
                if (aValid != bValid) return aValid ? -1 : 1;
                return ParseVersion(b.Version).CompareTo(ParseVersion(a.Version));
            });

            // Populate version list
            PopulateVersionList();

            // Select first version by default
            if (_changelogEntries.Count > 0)
            {
                SelectVersion(_changelogEntries[0].Version);
            }
            else
            {
                ShowError(Loc.Get("changelog.noEntries"));
            }
        }
        catch (Exception ex)
        {
            ShowError(Loc.Get("changelog.loadError", ex.Message));
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateVersionList()
    {
        VersionListPanel.Children.Clear();

        foreach (var entry in _changelogEntries)
        {
            var button = new Button
            {
                Style = (Style)FindResource("VersionButton"),
                Tag = entry.Version,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var stack = new StackPanel();

            var versionText = new TextBlock
            {
                Text = $"v{entry.Version}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13
            };
            stack.Children.Add(versionText);

            if (entry.IsLatest)
            {
                var badge = new TextBlock
                {
                    Text = Loc.Get("changelog.latest"),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 204, 102)),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(badge);
            }
            else if (entry.IsCurrent)
            {
                var badge = new TextBlock
                {
                    Text = Loc.Get("changelog.current"),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 102, 255)),
                    Margin = new Thickness(0, 2, 0, 0)
                };
                stack.Children.Add(badge);
            }

            var dateText = new TextBlock
            {
                Text = entry.Date.ToString("d", Loc.GetCulture()),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            stack.Children.Add(dateText);

            button.Content = stack;
            button.Click += VersionButton_Click;

            VersionListPanel.Children.Add(button);
        }
    }

    private void VersionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string version)
        {
            SelectVersion(version);
        }
    }

    private void SelectVersion(string version)
    {
        // Update button states
        foreach (Button button in VersionListPanel.Children.OfType<Button>())
        {
            button.Background = button.Tag?.ToString() == version
                ? new SolidColorBrush(Color.FromArgb(26, 255, 255, 255))
                : Brushes.Transparent;
        }

        // Display content
        var entry = _changelogEntries.FirstOrDefault(e => e.Version == version);
        if (entry != null)
        {
            DisplayChangelog(entry);
        }
    }

    private void DisplayChangelog(ChangelogEntry entry)
    {
        ChangelogContent.Children.Clear();
        ChangelogContent.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;

        // Version header
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        var versionTitle = new TextBlock
        {
            Text = Loc.Get("changelog.version", entry.Version),
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = VNotch.Services.UiPalette.PrimaryBrush
        };
        headerStack.Children.Add(versionTitle);

        var dateText = new TextBlock
        {
            Text = entry.Date.ToString("D", Loc.GetCulture()),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
            Margin = new Thickness(0, 4, 0, 0)
        };
        headerStack.Children.Add(dateText);

        if (entry.IsLatest || entry.IsCurrent)
        {
            var badgeStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };

            if (entry.IsLatest)
            {
                var latestBadge = CreateBadge(Loc.Get("changelog.latest"), Color.FromRgb(0, 204, 102));
                badgeStack.Children.Add(latestBadge);
            }

            if (entry.IsCurrent)
            {
                var currentBadge = CreateBadge(Loc.Get("changelog.current"), Color.FromRgb(0, 102, 255));
                badgeStack.Children.Add(currentBadge);
                if (entry.IsLatest) currentBadge.Margin = new Thickness(8, 0, 0, 0);
            }

            headerStack.Children.Add(badgeStack);
        }

        ChangelogContent.Children.Add(headerStack);

        // Divider
        var divider = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(42, 42, 42)),
            Margin = new Thickness(0, 0, 0, 20)
        };
        ChangelogContent.Children.Add(divider);

        // Parse and display markdown content
        ParseMarkdownContent(entry.Content);

        // Animate content transition
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(300);

        var opacityAnim = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
        ChangelogContent.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

        if (ChangelogContent.RenderTransform is not TranslateTransform)
        {
            ChangelogContent.RenderTransform = new TranslateTransform();
        }
        var translateAnim = new DoubleAnimation(15, 0, duration) { EasingFunction = ease };
        ChangelogContent.RenderTransform.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    private Border CreateBadge(string text, Color color)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color)
            }
        };
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
            InlineCodeFont = SetupFonts.SFProDisplayFont,
            BodyFontSize = 13,
            BodyLineHeight = 20,
            BodyFontWeight = FontWeights.Normal,
            ParagraphMargin = new Thickness(0, 4, 0, 4),
            SoftBreakAsSpace = false,
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

    private void ParseMarkdownContent(string content)
    {
        RenderMarkdownContent(ChangelogContent, content);
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

    private static Version ParseVersion(string versionString)
    {
        try
        {
            if (UpdateService.TryParseReleaseVersion(versionString, out var parsed, out _))
            {
                return parsed;
            }

            Span<char> clean = stackalloc char[Math.Min(versionString.Length, 64)];
            int len = 0;
            foreach (char ch in versionString)
            {
                if ((ch >= '0' && ch <= '9') || ch == '.')
                {
                    if (len < clean.Length)
                    {
                        clean[len++] = ch;
                    }
                }
            }

            return Version.Parse(clean[..len]);
        }
        catch (Exception)
        {
            // Fallback to version 0.0.0 on malformed version string
            return new Version(0, 0, 0);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        var ease = new QuarticEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(250);

        var opacityAnim = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        var scaleAnim = new DoubleAnimation(1, 0.95, duration) { EasingFunction = ease };
        var translateAnim = new DoubleAnimation(0, 20, duration) { EasingFunction = ease };

        opacityAnim.Completed += (s, ev) => Close();

        MainShell.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        ShellTranslate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int exStyle = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE);
            Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, exStyle | Win32Interop.WS_EX_TOOLWINDOW);
        }
    }

    private sealed class ChangelogEntry
    {
        public string Version { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string Content { get; set; } = string.Empty;
        public bool IsLatest { get; set; }
        public bool IsCurrent { get; set; }
    }
}
