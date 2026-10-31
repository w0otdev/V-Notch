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

        // 2. Try disk file in BaseDirectory, parent, or docs/ subdirectories
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var localPath = Path.Combine(baseDir, fileName);
            if (File.Exists(localPath))
            {
                return File.ReadAllText(localPath);
            }

            var localDocPath = Path.Combine(baseDir, "docs", fileName);
            if (File.Exists(localDocPath))
            {
                return File.ReadAllText(localDocPath);
            }

            var parentDir = Directory.GetParent(baseDir)?.FullName;
            if (parentDir != null)
            {
                var parentPath = Path.Combine(parentDir, fileName);
                if (File.Exists(parentPath))
                {
                    return File.ReadAllText(parentPath);
                }

                var parentDocPath = Path.Combine(parentDir, "docs", fileName);
                if (File.Exists(parentDocPath))
                {
                    return File.ReadAllText(parentDocPath);
                }
            }
        }
        catch
        {
            // Fall through
        }

        // 3. Fallback text
        return GetFallbackTermsMarkdown(isVietnamese);
    }

    internal static string GetFallbackTermsMarkdown(bool isVietnamese)
    {
        return isVietnamese
            ? "# V-Notch - Điều khoản sử dụng\n\nV-Notch được cấp phép theo Apache License 2.0. Theo Điều 4 Apache-2.0, bạn được sử dụng, sửa đổi, fork, phân phối lại và bán bản sao phần mềm với điều kiện giữ nguyên thông báo bản quyền, giấy phép và ghi rõ các thay đổi đáng kể trên tệp đã sửa; các bản fork không bắt buộc phải giữ tên sản phẩm gốc. Bản sửa đổi không được mạo danh bản phát hành chính thức của w0otdev. Xem Chính sách bảo mật và điều khoản của các dịch vụ bên thứ ba khi bật tính năng kết nối mạng tùy chọn."
            : "# V-Notch - Terms of Use\n\nV-Notch is licensed under the Apache License 2.0. Under Apache-2.0 Section 4, you may use, modify, fork, redistribute, and sell copies of the software provided you retain copyright and license notices and state significant file changes; forks are not required to keep a specific product name. Modified builds must not misrepresent themselves as official w0otdev releases. Review the Privacy Policy and applicable third-party service terms before enabling optional network features.";
    }

    private void RenderMarkdownToPanel(string markdown)
    {
        RenderMarkdownToPanel(_termsContentPanel, markdown);
    }

    internal static void RenderMarkdownToPanel(StackPanel targetPanel, string markdown)
    {
        targetPanel.Children.Clear();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        try
        {
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
                targetPanel.Children.Add(tb);
            }

            var blocks = VNotchNative.TokenizeMarkdownBlocks(markdown);
            foreach (var block in blocks)
            {
                string textSlice = block.TextLen > 0
                    ? markdown.Substring(block.TextStart, block.TextLen)
                    : string.Empty;

                switch (block.BlockKind)
                {
                    case VnMarkdownBlockKind.Empty:
                        FlushParagraph();
                        break;

                    case VnMarkdownBlockKind.HorizontalRule:
                        {
                            FlushParagraph();
                            var hr = new Border
                            {
                                Height = 1,
                                Background = Freeze(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255))),
                                Margin = new Thickness(0, 10, 0, 10)
                            };
                            targetPanel.Children.Add(hr);
                            break;
                        }

                    case VnMarkdownBlockKind.Heading:
                        {
                            FlushParagraph();
                            double fontSize = block.Level switch
                            {
                                1 => 18,
                                2 => 15,
                                3 => 13.5,
                                _ => 13
                            };
                            Thickness margin = block.Level switch
                            {
                                1 => new Thickness(0, 4, 0, 10),
                                2 => new Thickness(0, 24, 0, 8),
                                3 => new Thickness(0, 16, 0, 6),
                                _ => new Thickness(0, 12, 0, 4)
                            };
                            var heading = new TextBlock
                            {
                                Text = textSlice,
                                FontSize = fontSize,
                                FontWeight = FontWeights.Bold,
                                Foreground = BrushWhite,
                                FontFamily = SFProBold,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = margin
                            };
                            targetPanel.Children.Add(heading);
                            break;
                        }

                    case VnMarkdownBlockKind.BulletItem:
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
                            AddFormattedInlines(itemText, textSlice);
                            Grid.SetColumn(itemText, 1);
                            bulletGrid.Children.Add(itemText);

                            targetPanel.Children.Add(bulletGrid);
                            break;
                        }

                    case VnMarkdownBlockKind.ChecklistItem:
                        {
                            FlushParagraph();
                            var checkGrid = new Grid
                            {
                                Margin = new Thickness(0, 0, 0, 8)
                            };
                            checkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                            checkGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                            var checkMark = new TextBlock
                            {
                                Text = block.Checked ? "☑" : "☐",
                                FontSize = 13,
                                FontWeight = FontWeights.Bold,
                                FontFamily = SFProBold,
                                Foreground = block.Checked ? BrushGreen : BrushMuted,
                                VerticalAlignment = VerticalAlignment.Top
                            };
                            Grid.SetColumn(checkMark, 0);
                            checkGrid.Children.Add(checkMark);

                            var itemText = new TextBlock
                            {
                                FontSize = 13,
                                FontWeight = FontWeights.Bold,
                                LineHeight = 22,
                                Foreground = BrushDimWhite,
                                FontFamily = SFProBold,
                                TextWrapping = TextWrapping.Wrap
                            };
                            AddFormattedInlines(itemText, textSlice);
                            Grid.SetColumn(itemText, 1);
                            checkGrid.Children.Add(itemText);

                            targetPanel.Children.Add(checkGrid);
                            break;
                        }

                    case VnMarkdownBlockKind.NumberedItem:
                        {
                            FlushParagraph();
                            string markerSlice = block.MarkerLen > 0
                                ? markdown.Substring(block.MarkerStart, block.MarkerLen)
                                : $"{block.Level}.";

                            var numGrid = new Grid
                            {
                                Margin = new Thickness(0, 0, 0, 8)
                            };
                            numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                            numGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                            var num = new TextBlock
                            {
                                Text = markerSlice,
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
                            AddFormattedInlines(itemText, textSlice);
                            Grid.SetColumn(itemText, 1);
                            numGrid.Children.Add(itemText);

                            targetPanel.Children.Add(numGrid);
                            break;
                        }

                    case VnMarkdownBlockKind.Blockquote:
                        {
                            FlushParagraph();
                            var quoteText = new TextBlock
                            {
                                FontSize = 13,
                                FontWeight = FontWeights.Bold,
                                LineHeight = 22,
                                Foreground = BrushMuted,
                                FontFamily = SFProBold,
                                TextWrapping = TextWrapping.Wrap
                            };
                            AddFormattedInlines(quoteText, textSlice);

                            var quoteBorder = new Border
                            {
                                BorderBrush = BrushContainerBorder,
                                BorderThickness = new Thickness(3, 0, 0, 0),
                                Padding = new Thickness(12, 4, 0, 4),
                                Margin = new Thickness(0, 0, 0, 10),
                                Child = quoteText
                            };
                            targetPanel.Children.Add(quoteBorder);
                            break;
                        }

                    case VnMarkdownBlockKind.TableSeparator:
                        FlushParagraph();
                        break;

                    case VnMarkdownBlockKind.TableRow:
                        {
                            FlushParagraph();
                            ReadOnlySpan<char> rowSpan = markdown.AsSpan(block.TextStart, block.TextLen);
                            var cells = VNotchNative.SplitMarkdownTableCells(rowSpan);
                            if (cells.Length == 0)
                            {
                                break;
                            }

                            var rowGrid = new Grid
                            {
                                Margin = new Thickness(0, 0, 0, 6)
                            };
                            for (int c = 0; c < cells.Length; c++)
                            {
                                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                                string cellText = cells[c].Len > 0
                                    ? rowSpan.Slice(cells[c].Start, cells[c].Len).ToString()
                                    : string.Empty;

                                var cellBlock = new TextBlock
                                {
                                    FontSize = 12.5,
                                    FontWeight = FontWeights.Bold,
                                    LineHeight = 20,
                                    Foreground = BrushDimWhite,
                                    FontFamily = SFProBold,
                                    TextWrapping = TextWrapping.Wrap,
                                    Margin = new Thickness(0, 0, 8, 0)
                                };
                                AddFormattedInlines(cellBlock, cellText);
                                Grid.SetColumn(cellBlock, c);
                                rowGrid.Children.Add(cellBlock);
                            }
                            targetPanel.Children.Add(rowGrid);
                            break;
                        }

                    case VnMarkdownBlockKind.Paragraph:
                    default:
                        if (!string.IsNullOrEmpty(textSlice))
                        {
                            currentParagraph.Add(textSlice);
                        }
                        if (block.HasHardBreak)
                        {
                            FlushParagraph();
                        }
                        break;
                }
            }

            FlushParagraph();
        }
        catch (RegexMatchTimeoutException)
        {
            targetPanel.Children.Clear();
            targetPanel.Children.Add(new TextBlock
            {
                Text = markdown,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushDimWhite,
                FontFamily = SFProBold,
                TextWrapping = TextWrapping.Wrap
            });
        }
        catch (Exception)
        {
            targetPanel.Children.Clear();
            targetPanel.Children.Add(new TextBlock
            {
                Text = markdown,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = BrushDimWhite,
                FontFamily = SFProBold,
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    internal static void AddFormattedInlines(TextBlock textBlock, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            var tokens = VNotchNative.TokenizeMarkdownInlines(text);
            foreach (var token in tokens)
            {
                if (token.TextLen <= 0)
                {
                    continue;
                }

                string partText = text.Substring(token.TextStart, token.TextLen);
                switch (token.InlineKind)
                {
                    case VnMarkdownInlineKind.Bold:
                        textBlock.Inlines.Add(new Run(partText)
                        {
                            FontWeight = FontWeights.Bold,
                            Foreground = BrushWhite
                        });
                        break;

                    case VnMarkdownInlineKind.Italic:
                        textBlock.Inlines.Add(new Run(partText)
                        {
                            FontStyle = FontStyles.Italic,
                            FontWeight = FontWeights.Bold,
                            Foreground = BrushDimWhite
                        });
                        break;

                    case VnMarkdownInlineKind.Code:
                        textBlock.Inlines.Add(new Run(partText)
                        {
                            FontFamily = new FontFamily("Consolas"),
                            FontWeight = FontWeights.Bold,
                            Foreground = BrushWhite
                        });
                        break;

                    case VnMarkdownInlineKind.Link:
                    case VnMarkdownInlineKind.Url:
                        {
                            string targetUrl = token.InlineKind == VnMarkdownInlineKind.Url
                                ? partText
                                : (token.AuxLen > 0 ? text.Substring(token.AuxStart, token.AuxLen) : string.Empty);

                            if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) &&
                                uri.Scheme == Uri.UriSchemeHttps)
                            {
                                var link = new Hyperlink(new Run(partText) { FontWeight = FontWeights.Bold })
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
                                textBlock.Inlines.Add(new Run(partText) { FontWeight = FontWeights.Bold });
                            }
                            break;
                        }

                    case VnMarkdownInlineKind.Text:
                    default:
                        textBlock.Inlines.Add(new Run(partText)
                        {
                            FontWeight = FontWeights.Bold
                        });
                        break;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            textBlock.Inlines.Clear();
            textBlock.Inlines.Add(new Run(text) { FontWeight = FontWeights.Bold });
        }
        catch (Exception)
        {
            textBlock.Inlines.Clear();
            textBlock.Inlines.Add(new Run(text) { FontWeight = FontWeights.Bold });
        }
    }
}
