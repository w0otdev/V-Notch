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
    private readonly Button _scrollToBottomButton;
    private readonly CheckBox _agreeCheckBox;

    private bool _hasReadToBottom;
    private string _currentLoadedLanguage = string.Empty;

    private static readonly FontFamily SFProBold = new("pack://application:,,,/Fonts/#SF Pro Display, SF Pro Display, Nirmala UI, Segoe UI Variable Display, Segoe UI, Inter, Roboto, Sans-serif");
    private static readonly FontFamily SFProText = SFProBold;

    private static readonly SolidColorBrush BrushWhite = Freeze(new SolidColorBrush(Colors.White));
    private static readonly SolidColorBrush BrushDimWhite = Freeze(new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)));
    private static readonly SolidColorBrush BrushMuted = Freeze(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)));
    private static readonly SolidColorBrush BrushGreen = Freeze(new SolidColorBrush(Color.FromArgb(255, 48, 209, 88)));
    private static readonly SolidColorBrush BrushAmber = Freeze(new SolidColorBrush(Color.FromArgb(255, 255, 159, 10)));
    private static readonly SolidColorBrush BrushContainerBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 18, 18, 22)));
    private static readonly SolidColorBrush BrushContainerBorder = Freeze(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public bool HasReadToBottom => _hasReadToBottom;
    public bool CanContinue => _hasReadToBottom && _agreeCheckBox.IsChecked == true;
    public event Action<bool>? CanContinueChanged;

    public ScrollViewer TermsScrollViewer => _termsScrollViewer;
    public CheckBox AgreeCheckBox => _agreeCheckBox;

    public TermsOfServicePage()
    {
        var mainGrid = new Grid();
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Row 0: Headline
        _headline = new TextBlock
        {
            Text = Loc.Get("setup.terms.headline"),
            FontSize = 28,
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
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
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

        _termsContentPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        _termsScrollViewer.Content = _termsContentPanel;
        _termsBorder.Child = _termsScrollViewer;

        Grid.SetRow(_termsBorder, 2);
        mainGrid.Children.Add(_termsBorder);

        // Row 3: Footer controls (Status, Scroll to bottom, and Agreement CheckBox)
        _footerPanel = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0)
        };
        _footerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _footerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _footerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _footerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Row 0 Col 0: Reading status
        _statusText = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            FontFamily = SFProBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_statusText, 0);
        Grid.SetColumn(_statusText, 0);
        _footerPanel.Children.Add(_statusText);

        // Row 0 Col 1: Scroll to bottom shortcut button
        _scrollToBottomButton = new Button
        {
            Content = Loc.Get("setup.terms.scrollToBottom"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            FontFamily = SFProBold,
            Padding = new Thickness(12, 6, 12, 6),
            Cursor = Cursors.Hand,
            Background = Freeze(new SolidColorBrush(Color.FromArgb(255, 30, 30, 36))),
            Foreground = BrushWhite,
            BorderThickness = new Thickness(1),
            BorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)))
        };

        var btnTemplate = new ControlTemplate(typeof(Button));
        var btnBorder = new FrameworkElementFactory(typeof(Border));
        btnBorder.Name = "border";
        btnBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        btnBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
        btnBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
        btnBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        btnBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        btnBorder.AppendChild(cp);
        btnTemplate.VisualTree = btnBorder;
        _scrollToBottomButton.Template = btnTemplate;

        _scrollToBottomButton.Click += ScrollToBottomButton_Click;
        Grid.SetRow(_scrollToBottomButton, 0);
        Grid.SetColumn(_scrollToBottomButton, 1);
        _footerPanel.Children.Add(_scrollToBottomButton);

        // Row 1 Col 0-1: Agreement CheckBox
        _agreeCheckBox = new CheckBox
        {
            Content = Loc.Get("setup.terms.checkbox"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            FontFamily = SFProBold,
            Foreground = BrushWhite,
            Margin = new Thickness(0, 10, 0, 0),
            IsEnabled = false,
            Cursor = Cursors.Hand
        };
        _agreeCheckBox.Checked += AgreeCheckBox_CheckedChanged;
        _agreeCheckBox.Unchecked += AgreeCheckBox_CheckedChanged;
        Grid.SetRow(_agreeCheckBox, 1);
        Grid.SetColumn(_agreeCheckBox, 0);
        Grid.SetColumnSpan(_agreeCheckBox, 2);
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
        _description.Text = Loc.Get("setup.terms.description");
        _agreeCheckBox.Content = Loc.Get("setup.terms.checkbox");
        _scrollToBottomButton.Content = Loc.Get("setup.terms.scrollToBottom");

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

    private void ScrollToBottomButton_Click(object sender, RoutedEventArgs e)
    {
        _termsScrollViewer.ScrollToEnd();
        // ScrollToEnd is queued by WPF. ScrollChanged confirms the actual offset.
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
            _scrollToBottomButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            _statusText.Text = "↓ " + Loc.Get("setup.terms.scrollHint");
            _statusText.Foreground = BrushAmber;
            _scrollToBottomButton.Visibility = Visibility.Visible;
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
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                LineHeight = 19,
                Foreground = BrushDimWhite,
                FontFamily = SFProBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
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
                    Foreground = BrushGreen,
                    FontFamily = SFProBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 14, 0, 6)
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
                    Margin = new Thickness(0, 10, 0, 4)
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
                    Margin = new Thickness(12, 1, 0, 4)
                };
                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                bulletGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = "•",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    FontFamily = SFProBold,
                    Foreground = BrushGreen,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -1, 0, 0)
                };
                Grid.SetColumn(dot, 0);
                bulletGrid.Children.Add(dot);

                var itemText = new TextBlock
                {
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    LineHeight = 19,
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
                    Margin = new Thickness(12, 1, 0, 4)
                };
                numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var num = new TextBlock
                {
                    Text = numMatch.Groups[1].Value + ".",
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    FontFamily = SFProBold,
                    Foreground = BrushMuted,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(num, 0);
                numGrid.Children.Add(num);

                var itemText = new TextBlock
                {
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    LineHeight = 19,
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
        }

        FlushParagraph();
    }

    private static void AddFormattedInlines(TextBlock textBlock, string text)
    {
        // Parse **bold** markers
        var parts = Regex.Split(text, @"(\*\*.*?\*\*)");
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
