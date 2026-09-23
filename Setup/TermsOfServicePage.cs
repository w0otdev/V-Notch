using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch;

public class TermsOfServicePage : UserControl, ISetupAnimatedPage
{
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly Border _termsBorder;
    private readonly ScrollViewer _termsScrollViewer;
    private readonly StackPanel _termsContentPanel;
    private readonly Grid _footerPanel;
    private readonly TextBlock _statusText;
    private readonly CheckBox _agreeCheckBox;

    private bool _hasReadToBottom;
    private string _currentLoadedLanguage = string.Empty;

    private static readonly FontFamily SFProBold = SetupFonts.SFProDisplayFont;
    private static readonly FontFamily SFProText = SFProBold;

    private static readonly SolidColorBrush BrushWhite = Freeze(new SolidColorBrush(VNotch.Services.UiPalette.PrimaryColor));
    private static readonly SolidColorBrush BrushDimWhite = Freeze(new SolidColorBrush(Color.FromRgb(173, 173, 173)));
    private static readonly SolidColorBrush BrushMuted = Freeze(new SolidColorBrush(Color.FromRgb(140, 149, 158)));
    private static readonly SolidColorBrush BrushGreen = Freeze(new SolidColorBrush(Color.FromArgb(255, 48, 209, 88)));
    private static readonly SolidColorBrush BrushContainerBg = Freeze(new SolidColorBrush(Color.FromRgb(8, 8, 8)));
    private static readonly SolidColorBrush BrushContainerBorder = Freeze(new SolidColorBrush(Color.FromRgb(36, 36, 36)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public bool HasReadToBottom => _hasReadToBottom;
    public bool CanContinue => _hasReadToBottom && _agreeCheckBox.IsChecked == true;
    public event Action<bool>? CanContinueChanged;

    public ScrollViewer TermsScrollViewer => _termsScrollViewer;
    public CheckBox AgreeCheckBox => _agreeCheckBox;

    public TermsOfServicePage()
    {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);

        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: Headline
        _headline = new TextBlock
        {
            Text = Loc.Get("setup.terms.headline"),
            FontSize = 24,
            LineHeight = 30,
            FontWeight = FontWeights.Bold,
            Foreground = BrushWhite,
            FontFamily = SFProBold,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_headline, 0);
        mainGrid.Children.Add(_headline);

        // Row 1: Description
        _description = new TextBlock
        {
            Text = Loc.Get("setup.terms.description"),
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            LineHeight = 20,
            Foreground = BrushDimWhite,
            FontFamily = SFProBold,
            Margin = new Thickness(0, 0, 0, 16),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_description, 1);
        mainGrid.Children.Add(_description);

        // Row 2: Terms scroll container
        _termsBorder = new Border
        {
            Background = BrushContainerBg,
            BorderBrush = BrushContainerBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(22, 18, 14, 18),
            ClipToBounds = true
        };

        _termsScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 8, 0)
        };
        _termsScrollViewer.ScrollChanged += TermsScrollViewer_ScrollChanged;
        _termsScrollViewer.PanningMode = PanningMode.VerticalOnly;
        System.Windows.Automation.AutomationProperties.SetName(_termsScrollViewer, Loc.Get("setup.terms.headline"));

        _termsContentPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        _termsScrollViewer.Content = _termsContentPanel;
        _termsBorder.Child = _termsScrollViewer;

        Grid.SetRow(_termsBorder, 2);
        mainGrid.Children.Add(_termsBorder);

        // Row 3: Footer controls (Status and Agreement CheckBox)
        _footerPanel = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0)
        };
        _footerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _footerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: Reading status
        _statusText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            FontFamily = SFProBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_statusText, 0);
        _footerPanel.Children.Add(_statusText);

        // Row 1: Agreement CheckBox
        _agreeCheckBox = new CheckBox
        {
            Content = Loc.Get("setup.terms.checkbox"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            FontFamily = SFProBold,
            Foreground = BrushWhite,
            Margin = new Thickness(0, 16, 0, 0),
            IsEnabled = false,
            Cursor = Cursors.Hand
        };
        var agreementText = new FrameworkElementFactory(typeof(TextBlock));
        agreementText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        agreementText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        agreementText.SetValue(TextBlock.LineHeightProperty, 20.0);
        agreementText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        agreementText.SetValue(TextBlock.FontFamilyProperty, SFProBold);
        _agreeCheckBox.ContentTemplate = new DataTemplate { VisualTree = agreementText };
        _agreeCheckBox.Checked += AgreeCheckBox_CheckedChanged;
        _agreeCheckBox.Unchecked += AgreeCheckBox_CheckedChanged;
        Grid.SetRow(_agreeCheckBox, 1);
        _footerPanel.Children.Add(_agreeCheckBox);

        Grid.SetRow(_footerPanel, 3);
        mainGrid.Children.Add(_footerPanel);

        Content = mainGrid;

        LoadAndRenderTerms(Loc.CurrentLanguage);
        UpdateStatusUi();
    }

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        return new UIElement[] { _headline, _description, _termsBorder, _footerPanel };
    }

    public void RefreshLocalization()
    {
        _headline.Text = Loc.Get("setup.terms.headline");
        System.Windows.Automation.AutomationProperties.SetName(_termsScrollViewer, _headline.Text);
        _description.Text = Loc.Get("setup.terms.description");
        _agreeCheckBox.Content = Loc.Get("setup.terms.checkbox");

        var currentLang = Loc.CurrentLanguage;
        if (_currentLoadedLanguage != currentLang)
        {
            LoadAndRenderTerms(currentLang);
        }

        UpdateStatusUi();
    }

    private void TermsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        CheckIfScrolledToBottom();
    }

    public void CheckIfScrolledToBottom()
    {
        if (_hasReadToBottom)
        {
            return;
        }

        // The page is constructed before it is displayed. Before layout, both
        // extent and scrollable height are zero; that does not mean it was read.
        if (!IsMeasureValid || !IsArrangeValid ||
            !_termsScrollViewer.IsMeasureValid || !_termsScrollViewer.IsArrangeValid ||
            _termsScrollViewer.ViewportHeight <= 0 || _termsScrollViewer.ExtentHeight <= 0)
        {
            return;
        }

        // If content fits completely without scroll
        if (_termsScrollViewer.ScrollableHeight <= 0)
        {
            MarkAsReadToBottom();
            return;
        }

        // Allow only sub-pixel rounding, not an unread final line.
        if (_termsScrollViewer.VerticalOffset >= _termsScrollViewer.ScrollableHeight - 1)
        {
            MarkAsReadToBottom();
        }
    }

    private void MarkAsReadToBottom()
    {
        if (_hasReadToBottom)
        {
            return;
        }

        _hasReadToBottom = true;
        _agreeCheckBox.IsEnabled = true;
        _agreeCheckBox.IsChecked = true;
        UpdateStatusUi();
        CanContinueChanged?.Invoke(CanContinue);
    }

    private void AgreeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        CanContinueChanged?.Invoke(CanContinue);
    }

    private void UpdateStatusUi()
    {
        if (_hasReadToBottom)
        {
            _statusText.Text = "✓ " + Loc.Get("setup.terms.scrollCompleted");
            _statusText.Foreground = BrushGreen;
        }
        else
        {
            _statusText.Text = "↓ " + Loc.Get("setup.terms.scrollHint");
            _statusText.Foreground = BrushMuted;
        }
    }

    private void LoadAndRenderTerms(string language)
    {
        _currentLoadedLanguage = language;
        _hasReadToBottom = false;
        _agreeCheckBox.IsChecked = false;
        _agreeCheckBox.IsEnabled = false;
        _termsScrollViewer.ScrollToTop();
        CanContinueChanged?.Invoke(false);
        string markdown = LoadTermsMarkdown(language);
        RenderMarkdownToPanel(markdown);

        // Check immediately in case height fits
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            CheckIfScrolledToBottom();
        });
    }

    private static string LoadTermsMarkdown(string language)
    {
        bool isVietnamese = string.Equals(language, "vi", StringComparison.OrdinalIgnoreCase);
        string resourceName = isVietnamese ? "VNotch.TERMS_OF_SERVICE_VI.md" : "VNotch.TERMS_OF_SERVICE.md";
        string fileName = isVietnamese ? "TERMS_OF_SERVICE_VI.md" : "TERMS_OF_SERVICE.md";

        // 1. Try embedded resource
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        catch
        {
            // Fall through
        }

        // 2. Try disk file in BaseDirectory or parent
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var localPath = Path.Combine(baseDir, fileName);
            if (File.Exists(localPath))
            {
                return File.ReadAllText(localPath);
            }

            var parentDir = Directory.GetParent(baseDir)?.FullName;
            if (parentDir != null)
            {
                var parentPath = Path.Combine(parentDir, fileName);
                if (File.Exists(parentPath))
                {
                    return File.ReadAllText(parentPath);
                }
            }
        }
        catch
        {
            // Fall through
        }

        // 3. Fallback text
        return isVietnamese
            ? "# Điều Khoản Dịch Vụ — V-Notch\n\nBằng việc cài đặt và sử dụng V-Notch, bạn đồng ý tuân thủ các quy định về bản quyền Apache-2.0, không tái phân phối dưới tên riêng, tôn trọng quyền riêng tư cục bộ và các điều khoản tích hợp bên thứ ba."
            : "# Terms of Service — V-Notch\n\nBy installing and using V-Notch, you agree to comply with Apache-2.0 licensing, strict prohibition of redistribution under your own name, local-first system permissions, and third-party terms.";
    }

    private void RenderMarkdownToPanel(string markdown)
    {
        _termsContentPanel.Children.Clear();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var currentParagraph = new List<string>();

        void FlushParagraph()
        {
            if (currentParagraph.Count == 0) return;

            string fullText = string.Join(" ", currentParagraph).Trim();
            currentParagraph.Clear();

            if (string.IsNullOrWhiteSpace(fullText)) return;

            var tb = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                LineHeight = 22,
                Foreground = BrushDimWhite,
                FontFamily = SFProBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            AddFormattedInlines(tb, fullText);
            _termsContentPanel.Children.Add(tb);
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            // Horizontal Rule
            if (line == "---" || line == "***")
            {
                FlushParagraph();
                var hr = new Border
                {
                    Height = 1,
                    Background = Freeze(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255))),
                    Margin = new Thickness(0, 10, 0, 10)
                };
                _termsContentPanel.Children.Add(hr);
                continue;
            }

            // H1
            if (line.StartsWith("# "))
            {
                FlushParagraph();
                var h1 = new TextBlock
                {
                    Text = line.Substring(2).Trim(),
                    FontSize = 18,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushWhite,
                    FontFamily = SFProBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 10)
                };
                _termsContentPanel.Children.Add(h1);
                continue;
            }

            // H2
            if (line.StartsWith("## "))
            {
                FlushParagraph();
                var h2 = new TextBlock
                {
                    Text = line.Substring(3).Trim(),
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushWhite,
                    FontFamily = SFProBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 24, 0, 8)
                };
                _termsContentPanel.Children.Add(h2);
                continue;
            }

            // H3
            if (line.StartsWith("### "))
            {
                FlushParagraph();
                var h3 = new TextBlock
                {
                    Text = line.Substring(4).Trim(),
                    FontSize = 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushWhite,
                    FontFamily = SFProBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 16, 0, 6)
                };
                _termsContentPanel.Children.Add(h3);
                continue;
            }

            // Bullet item
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                FlushParagraph();
                var bulletGrid = new Grid
                {
                    Margin = new Thickness(0, 0, 0, 8)
                };
                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = "•",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    FontFamily = SFProBold,
                    Foreground = BrushWhite,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -1, 0, 0)
                };
                Grid.SetColumn(dot, 0);
                bulletGrid.Children.Add(dot);

                var itemText = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    LineHeight = 22,
                    Foreground = BrushDimWhite,
                    FontFamily = SFProBold,
                    TextWrapping = TextWrapping.Wrap
                };
                AddFormattedInlines(itemText, line.Substring(2).Trim());
                Grid.SetColumn(itemText, 1);
                bulletGrid.Children.Add(itemText);

                _termsContentPanel.Children.Add(bulletGrid);
                continue;
            }

            // Numbered item: e.g. "1. " or "2. "
            var numMatch = Regex.Match(line, @"^(\d+)\.\s+(.*)$");
            if (numMatch.Success)
            {
                FlushParagraph();
                var numGrid = new Grid
                {
                    Margin = new Thickness(0, 0, 0, 8)
                };
                numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var num = new TextBlock
                {
                    Text = numMatch.Groups[1].Value + ".",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    FontFamily = SFProBold,
                    Foreground = BrushMuted,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(num, 0);
                numGrid.Children.Add(num);

                var itemText = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    LineHeight = 22,
                    Foreground = BrushDimWhite,
                    FontFamily = SFProBold,
                    TextWrapping = TextWrapping.Wrap
                };
                AddFormattedInlines(itemText, numMatch.Groups[2].Value.Trim());
                Grid.SetColumn(itemText, 1);
                numGrid.Children.Add(itemText);

                _termsContentPanel.Children.Add(numGrid);
                continue;
            }

            // Regular paragraph line
            currentParagraph.Add(line);
            if (rawLine.EndsWith("  ", StringComparison.Ordinal)) FlushParagraph();
        }

        FlushParagraph();
    }

    private static void AddFormattedInlines(TextBlock textBlock, string text)
    {
        // Keep emphasis and links readable without exposing Markdown syntax.
        var parts = Regex.Split(text, @"(\*\*.*?\*\*|\[[^\]]+\]\([^)]+\)|`[^`]+`)");
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (part.StartsWith("**") && part.EndsWith("**") && part.Length >= 4)
            {
                string boldText = part.Substring(2, part.Length - 4);
                textBlock.Inlines.Add(new Run(boldText)
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushWhite
                });
            }
            else if (Regex.Match(part, @"^\[([^\]]+)\]\(([^)]+)\)$") is { Success: true } linkMatch)
            {
                string label = linkMatch.Groups[1].Value;
                if (Uri.TryCreate(linkMatch.Groups[2].Value, UriKind.Absolute, out var uri) &&
                    uri.Scheme == Uri.UriSchemeHttps)
                {
                    var link = new Hyperlink(new Run(label) { FontWeight = FontWeights.Bold })
                    {
                        NavigateUri = uri,
                        Foreground = BrushWhite,
                        ToolTip = uri.AbsoluteUri
                    };
                    link.RequestNavigate += (_, e) =>
                    {
                        SafeLauncher.TryOpenUrl(e.Uri);
                        e.Handled = true;
                    };
                    textBlock.Inlines.Add(link);
                }
                else
                {
                    textBlock.Inlines.Add(new Run(label) { FontWeight = FontWeights.Bold });
                }
            }
            else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
            {
                textBlock.Inlines.Add(new Run(part.Substring(1, part.Length - 2))
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    Foreground = BrushWhite
                });
            }
            else
            {
                textBlock.Inlines.Add(new Run(part)
                {
                    FontWeight = FontWeights.Bold
                });
            }
        }
    }
}
