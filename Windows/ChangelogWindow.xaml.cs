using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Windows;

public partial class ChangelogWindow : Window
{
    private readonly IUpdateService _updateService;
    private List<ChangelogEntry> _changelogEntries = new();
    private string? _selectedVersion;

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
                    Date = DateTime.Now,
                    Content = Loc.Get("changelog.currentInstalled"),
                    IsCurrent = true
                });
            }

            // Sort by version (descending)
            _changelogEntries = _changelogEntries.OrderByDescending(e => ParseVersion(e.Version)).ToList();

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
        _selectedVersion = version;

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
            Foreground = Brushes.White
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

    private void ParseMarkdownContent(string content)
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
            ChangelogContent.Children.Add(emptyText);
            return;
        }

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            if (string.IsNullOrEmpty(trimmedLine))
            {
                // Add spacing
                ChangelogContent.Children.Add(new TextBlock { Height = 8 });
                continue;
            }

            // Heading
            if (trimmedLine.StartsWith("###"))
            {
                var text = trimmedLine.TrimStart('#').Trim();
                ChangelogContent.Children.Add(CreateHeading(text, 16, FontWeights.SemiBold));
            }
            else if (trimmedLine.StartsWith("##"))
            {
                var text = trimmedLine.TrimStart('#').Trim();
                ChangelogContent.Children.Add(CreateHeading(text, 18, FontWeights.Bold));
            }
            else if (trimmedLine.StartsWith("#"))
            {
                var text = trimmedLine.TrimStart('#').Trim();
                ChangelogContent.Children.Add(CreateHeading(text, 20, FontWeights.Bold));
            }
            // Bullet point
            else if (trimmedLine.StartsWith("-") || trimmedLine.StartsWith("*"))
            {
                var text = trimmedLine.Substring(1).Trim();
                ChangelogContent.Children.Add(CreateBulletPoint(text));
            }
            // Regular paragraph
            else
            {
                ChangelogContent.Children.Add(CreateParagraph(trimmedLine));
            }
        }
    }

    private TextBlock CreateHeading(string text, double fontSize, FontWeight weight)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 12, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        ParseInlineMarkdown(tb, text);
        return tb;
    }

    private Grid CreateBulletPoint(string text)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 4)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bullet = new TextBlock
        {
            Text = "•",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0, 102, 255)),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetColumn(bullet, 0);
        grid.Children.Add(bullet);

        var content = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };
        ParseInlineMarkdown(content, text);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        return grid;
    }

    private TextBlock CreateParagraph(string text)
    {
        var tb = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 4, 0, 4)
        };
        ParseInlineMarkdown(tb, text);
        return tb;
    }

    private void ParseInlineMarkdown(TextBlock textBlock, string text)
    {
        textBlock.Inlines.Clear();
        var pattern = @"(\*\*.*?\*\*)|(?<!\w)(\*.*?\*)(?!\w)|(`.*?`)|(\[.*?\]\(.*?\))";

        var matches = Regex.Matches(text, pattern);
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
            {
                textBlock.Inlines.Add(new Run(text.Substring(lastIndex, match.Index - lastIndex)));
            }

            string value = match.Value;
            if (value.StartsWith("**") && value.EndsWith("**"))
            {
                textBlock.Inlines.Add(new Bold(new Run(value.Substring(2, value.Length - 4))));
            }
            else if (value.StartsWith("*") && value.EndsWith("*"))
            {
                textBlock.Inlines.Add(new Italic(new Run(value.Substring(1, value.Length - 2))));
            }
            else if (value.StartsWith("`") && value.EndsWith("`"))
            {
                var run = new Run(value.Substring(1, value.Length - 2))
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                    Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230))
                };
                textBlock.Inlines.Add(run);
            }
            else if (value.StartsWith("["))
            {
                var linkMatch = Regex.Match(value, @"\[(.*?)\]\((.*?)\)");
                if (linkMatch.Success)
                {
                    var hyperlink = new Hyperlink(new Run(linkMatch.Groups[1].Value));
                    hyperlink.NavigateUri = new Uri(linkMatch.Groups[2].Value);
                    hyperlink.Foreground = new SolidColorBrush(Color.FromRgb(0, 102, 255));
                    hyperlink.TextDecorations = null;
                    hyperlink.RequestNavigate += (s, e) =>
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = e.Uri.AbsoluteUri,
                            UseShellExecute = true
                        });
                        e.Handled = true;
                    };
                    textBlock.Inlines.Add(hyperlink);
                }
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            textBlock.Inlines.Add(new Run(text.Substring(lastIndex)));
        }
    }

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
            var cleanVersion = Regex.Replace(versionString, @"[^\d\.]", "");
            return Version.Parse(cleanVersion);
        }
        catch
        {
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

    private class ChangelogEntry
    {
        public string Version { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string Content { get; set; } = string.Empty;
        public bool IsLatest { get; set; }
        public bool IsCurrent { get; set; }
    }
}
