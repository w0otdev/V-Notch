using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VNotch;

public class LanguagePage : UserControl, ISetupAnimatedPage
{
    private readonly TextBlock _headline;
    private readonly TextBlock _description;
    private readonly ScrollViewer _languageListScrollViewer;
    private readonly Dictionary<string, Border> _cards = new();
    private string _selectedLanguage = "en";

    private class LanguageMetadata
    {
        public string Flag { get; set; } = "";
        public string Name { get; set; } = "";
        public string NativeName { get; set; } = "";
        public string Code { get; set; } = "";
    }

    private static readonly List<LanguageMetadata> AvailableLanguages = new()
    {
        new LanguageMetadata { Flag = "en", Name = "English",    NativeName = "English",    Code = "en" },
        new LanguageMetadata { Flag = "vi", Name = "Vietnamese", NativeName = "Tiếng Việt", Code = "vi" },
        new LanguageMetadata { Flag = "zh", Name = "Chinese",    NativeName = "中文",        Code = "zh" },
        new LanguageMetadata { Flag = "pt", Name = "Portuguese", NativeName = "Português",   Code = "pt" },
        new LanguageMetadata { Flag = "ru", Name = "Russian",    NativeName = "Русский",    Code = "ru" },
        new LanguageMetadata { Flag = "ar", Name = "Arabic",     NativeName = "العربية",    Code = "ar" },
        new LanguageMetadata { Flag = "ko", Name = "Korean",     NativeName = "한국어",      Code = "ko" },
        new LanguageMetadata { Flag = "es", Name = "Spanish",    NativeName = "Español",    Code = "es" },
        new LanguageMetadata { Flag = "fr", Name = "French",     NativeName = "Français",   Code = "fr" },
        new LanguageMetadata { Flag = "de", Name = "German",     NativeName = "Deutsch",    Code = "de" },
        new LanguageMetadata { Flag = "ja", Name = "Japanese",   NativeName = "日本語",      Code = "ja" },
        new LanguageMetadata { Flag = "hi", Name = "Hindi",      NativeName = "हिन्दी",     Code = "hi" },
        new LanguageMetadata { Flag = "it", Name = "Italian",    NativeName = "Italiano",   Code = "it" },
        new LanguageMetadata { Flag = "tr", Name = "Turkish",    NativeName = "Türkçe",     Code = "tr" },
        new LanguageMetadata { Flag = "pl", Name = "Polish",     NativeName = "Polski",     Code = "pl" },
        new LanguageMetadata { Flag = "nl", Name = "Dutch",      NativeName = "Nederlands", Code = "nl" },
        new LanguageMetadata { Flag = "id", Name = "Indonesian", NativeName = "Indonesia",  Code = "id" },
    };

    private static readonly FontFamily SFProBold = SetupFonts.SFProDisplayFont;

    /// <summary>Loads a flag PNG from the embedded WPF resource stream. Falls back to a blank bitmap in headless/test environments.</summary>
    private static System.Windows.Media.Imaging.BitmapImage LoadFlagBitmap(string langCode)
    {
        try
        {
            if (!UriParser.IsKnownScheme("pack"))
                _ = System.IO.Packaging.PackUriHelper.UriSchemePack;

            var uri = new Uri($"pack://application:,,,/Assets/Flags/flag_{langCode}.png", UriKind.Absolute);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // In headless test environments the pack resource stream is unavailable;
            // return a 1×1 transparent bitmap so layout still works without crashing.
            var fallback = new System.Windows.Media.Imaging.BitmapImage();
            fallback.Freeze();
            return fallback;
        }
    }

    // Palette
    private static readonly SolidColorBrush BrushCardBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 20, 20, 24)));
    private static readonly SolidColorBrush BrushCardBorder = Freeze(new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)));
    private static readonly SolidColorBrush BrushSelectedBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 30, 30, 38)));
    private static readonly SolidColorBrush BrushSelectedBorder = Freeze(new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)));
    private static readonly SolidColorBrush BrushHoverBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 26, 26, 32)));
    private static readonly SolidColorBrush BrushWhite = Freeze(new SolidColorBrush(VNotch.Services.UiPalette.PrimaryColor));
    private static readonly SolidColorBrush BrushSubtle = Freeze(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
    private static readonly SolidColorBrush BrushFlagBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 38, 38, 46)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public string SelectedLanguage => _selectedLanguage;
    public event Action<string>? LanguageChanged;

    public LanguagePage(string initialLanguage = "en")
    {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);

        _selectedLanguage = initialLanguage;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _headline = new TextBlock
        {
            Text = VNotch.Services.Loc.Get("setup.language.headline"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = BrushWhite,
            FontFamily = SFProBold,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);

        _description = new TextBlock
        {
            Text = VNotch.Services.Loc.Get("setup.language.description"),
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
            FontFamily = SFProBold,
            Margin = new Thickness(0, 0, 0, 20),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_description, 1);
        grid.Children.Add(_description);

        // Slim single-column rows in a scrollable list
        var listStack = new StackPanel { Orientation = Orientation.Vertical };

        foreach (var lang in AvailableLanguages)
        {
            var row = CreateLanguageRow(lang.Flag, lang.NativeName, lang.Name, lang.Code);
            listStack.Children.Add(row);
            _cards[lang.Code] = row;
        }

        _languageListScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 0, 4, 0),
            Content = listStack
        };
        Grid.SetRow(_languageListScrollViewer, 2);
        grid.Children.Add(_languageListScrollViewer);

        UpdateSelectionVisuals(animate: false);
        Content = grid;

        Loaded += (_, _) =>
        {
            if (_cards.TryGetValue(_selectedLanguage, out var sel))
                sel.BringIntoView();
        };
    }

    internal ScrollViewer LanguageListScrollViewer => _languageListScrollViewer;
    internal bool HasLanguageOption(string languageCode) => _cards.ContainsKey(languageCode);

    public IReadOnlyList<UIElement> GetAnimatedElements()
    {
        var elements = new List<UIElement> { _headline, _description };
        foreach (var lang in AvailableLanguages)
        {
            if (_cards.TryGetValue(lang.Code, out var card))
                elements.Add(card);
        }
        return elements;
    }

    /// <summary>Creates a single slim language row: circle flag | native name (bold) + english name (muted) | checkmark</summary>
    private Border CreateLanguageRow(string flagKey, string nativeName, string englishName, string langCode)
    {
        // Real PNG flag clipped to a circle
        var flagBitmap = LoadFlagBitmap(flagKey);
        var flagImage = new System.Windows.Controls.Image
        {
            Source = flagBitmap,
            Width = 40,
            Height = 30,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Clip the image to an ellipse for a round badge
        var ellipseClip = new System.Windows.Media.EllipseGeometry
        {
            Center = new Point(21, 21),
            RadiusX = 21,
            RadiusY = 21
        };
        var flagCircle = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = BrushFlagBg,
            Margin = new Thickness(0, 0, 14, 0),
            ClipToBounds = true,
            Child = flagImage
        };

        var nativeBlock = new TextBlock
        {
            Text = nativeName,
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushWhite,
            FontFamily = SFProBold,
            Margin = new Thickness(0, 0, 0, 1),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var englishBlock = new TextBlock
        {
            Text = englishName,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushSubtle,
            FontFamily = SFProBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(nativeBlock);
        textStack.Children.Add(englishBlock);

        var checkmark = new TextBlock
        {
            Text = "✓",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = BrushWhite,
            FontFamily = SFProBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.5, 0.5),
            Tag = "checkmark"
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(flagCircle, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(checkmark, 2);
        row.Children.Add(flagCircle);
        row.Children.Add(textStack);
        row.Children.Add(checkmark);

        var border = new Border
        {
            Background = BrushCardBg,
            BorderBrush = BrushCardBorder,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 10, 16, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Tag = langCode,
            Child = row,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };

        border.MouseLeftButtonDown += (s, e) =>
        {
            if (_selectedLanguage != langCode)
            {
                _selectedLanguage = langCode;
                UpdateSelectionVisuals(animate: true);
            }
            LanguageChanged?.Invoke(langCode);
            e.Handled = true;
        };

        border.MouseEnter += (s, e) =>
        {
            if ((string)border.Tag != _selectedLanguage)
                AnimateBorderBackground(border, BrushHoverBg);
        };

        border.MouseLeave += (s, e) =>
        {
            if ((string)border.Tag != _selectedLanguage)
                AnimateBorderBackground(border, BrushCardBg);
        };

        return border;
    }

    private void UpdateSelectionVisuals(bool animate)
    {
        foreach (var kvp in _cards)
            UpdateCardVisual(kvp.Value, kvp.Key == _selectedLanguage, animate);
    }

    private static void UpdateCardVisual(Border card, bool isSelected, bool animate)
    {
        var targetBg = isSelected ? BrushSelectedBg : BrushCardBg;
        var targetBorder = isSelected ? BrushSelectedBorder : BrushCardBorder;

        if (animate)
        {
            var borderAnim = new ColorAnimation(
                ((SolidColorBrush)targetBorder).Color,
                TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var bgAnim = new ColorAnimation(
                ((SolidColorBrush)targetBg).Color,
                TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            if (card.BorderBrush is not SolidColorBrush || card.BorderBrush.IsFrozen)
                card.BorderBrush = new SolidColorBrush(((SolidColorBrush)card.BorderBrush).Color);
            if (card.Background is not SolidColorBrush || card.Background.IsFrozen)
                card.Background = new SolidColorBrush(((SolidColorBrush)card.Background).Color);

            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(borderAnim, VNotch.Services.AnimationConfig.TargetFps);
            ((SolidColorBrush)card.BorderBrush).BeginAnimation(SolidColorBrush.ColorProperty, borderAnim);
            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(bgAnim, VNotch.Services.AnimationConfig.TargetFps);
            ((SolidColorBrush)card.Background).BeginAnimation(SolidColorBrush.ColorProperty, bgAnim);

            if (isSelected && card.RenderTransform is ScaleTransform st)
            {
                var kf = new DoubleAnimationUsingKeyFrames();
                kf.KeyFrames.Add(new EasingDoubleKeyFrame(0.97,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(kf, VNotch.Services.AnimationConfig.TargetFps);
                st.BeginAnimation(ScaleTransform.ScaleXProperty, kf);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, kf);
            }
        }
        else
        {
            card.Background = targetBg;
            card.BorderBrush = targetBorder;
        }

        // Animate checkmark visibility
        if (card.Child is Grid g)
        {
            foreach (var child in g.Children)
            {
                if (child is TextBlock tb && tb.Tag as string == "checkmark")
                {
                    if (animate)
                    {
                        var targetOpacity = isSelected ? 1.0 : 0.0;
                        var targetScale = isSelected ? 1.0 : 0.5;

                        var fadeAnim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(180))
                        {
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                        };
                        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeAnim, VNotch.Services.AnimationConfig.TargetFps);
                        tb.BeginAnimation(OpacityProperty, fadeAnim);

                        if (tb.RenderTransform is ScaleTransform checkSt)
                        {
                            var scaleAnim = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(200))
                            {
                                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                            };
                            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(scaleAnim, VNotch.Services.AnimationConfig.TargetFps);
                            checkSt.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                            checkSt.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                        }
                    }
                    else
                    {
                        tb.Opacity = isSelected ? 1.0 : 0.0;
                        if (tb.RenderTransform is ScaleTransform checkSt)
                        {
                            checkSt.ScaleX = isSelected ? 1.0 : 0.5;
                            checkSt.ScaleY = isSelected ? 1.0 : 0.5;
                        }
                    }
                }
            }
        }
    }

    private static void AnimateBorderBackground(Border border, SolidColorBrush targetBrush)
    {
        if (border.Background is not SolidColorBrush || border.Background.IsFrozen)
            border.Background = new SolidColorBrush(((SolidColorBrush)border.Background).Color);

        var anim = new ColorAnimation(targetBrush.Color, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        ((SolidColorBrush)border.Background).BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    public void RefreshLocalization()
    {
        _headline.Text = VNotch.Services.Loc.Get("setup.language.headline");
        _description.Text = VNotch.Services.Loc.Get("setup.language.description");
    }
}
