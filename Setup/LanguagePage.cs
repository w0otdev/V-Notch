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
        public string FlagCode { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Code { get; set; } = "";
    }

    private static readonly List<LanguageMetadata> AvailableLanguages = new()
    {
        new LanguageMetadata { FlagCode = "US", Name = "English", Description = "Use V-Notch in English", Code = "en" },
        new LanguageMetadata { FlagCode = "VN", Name = "Tiếng Việt", Description = "Sử dụng V-Notch bằng tiếng Việt", Code = "vi" },
        new LanguageMetadata { FlagCode = "ES", Name = "Español", Description = "Usar V-Notch en español", Code = "es" },
        new LanguageMetadata { FlagCode = "FR", Name = "Français", Description = "Utiliser V-Notch en français", Code = "fr" },
        new LanguageMetadata { FlagCode = "DE", Name = "Deutsch", Description = "V-Notch auf Deutsch verwenden", Code = "de" },
        new LanguageMetadata { FlagCode = "JP", Name = "日本語", Description = "V-Notchを日本語で使用する", Code = "ja" },
        new LanguageMetadata { FlagCode = "IN", Name = "हिन्दी", Description = "V-Notch का उपयोग हिन्दी में करें", Code = "hi" }
    };

    private static readonly FontFamily SFProBold = new("pack://application:,,,/Fonts/#SF Pro Display, SF Pro Display, Nirmala UI, Segoe UI Variable Display, Segoe UI, Inter, Roboto, Sans-serif");
    private static readonly FontFamily SFProText = SFProBold;

    private static readonly SolidColorBrush BrushCardBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 22, 22, 26)));
    private static readonly SolidColorBrush BrushCardBorder = Freeze(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));
    private static readonly SolidColorBrush BrushSelectedBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 30, 30, 36)));
    private static readonly SolidColorBrush BrushSelectedBorder = Freeze(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)));
    private static readonly SolidColorBrush BrushHoverBg = Freeze(new SolidColorBrush(Color.FromArgb(255, 28, 28, 34)));
    private static readonly SolidColorBrush BrushWhite = Freeze(new SolidColorBrush(VNotch.Services.UiPalette.PrimaryColor));
    private static readonly SolidColorBrush BrushDimWhite = Freeze(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public string SelectedLanguage => _selectedLanguage;
    public event Action<string>? LanguageChanged;

    public LanguagePage(string initialLanguage = "en")
    {
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
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_headline, 0);
        grid.Children.Add(_headline);

        _description = new TextBlock
        {
            Text = VNotch.Services.Loc.Get("setup.language.description"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            LineHeight = 21,
            Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)),
            FontFamily = SFProBold,
            Margin = new Thickness(0, 0, 0, 28),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_description, 1);
        grid.Children.Add(_description);

        var languageCards = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 8, 0)
        };

        foreach (var lang in AvailableLanguages)
        {
            var card = CreateLanguageCard(lang.FlagCode, lang.Name, lang.Description, lang.Code);
            languageCards.Children.Add(card);
            _cards[lang.Code] = card;
        }

        _languageListScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            PanningMode = PanningMode.VerticalOnly,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Content = languageCards
        };
        Grid.SetRow(_languageListScrollViewer, 2);
        grid.Children.Add(_languageListScrollViewer);

        UpdateSelectionVisuals(animate: false);
        Content = grid;

        Loaded += (_, _) =>
        {
            if (_cards.TryGetValue(_selectedLanguage, out var selectedCard))
                selectedCard.BringIntoView();
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
            {
                elements.Add(card);
            }
        }
        return elements;
    }

    private Border CreateLanguageCard(string code, string title, string subtitle, string langCode)
    {
        var codeBadge = new Border
        {
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(255, 34, 34, 40)),
            Margin = new Thickness(0, 0, 16, 0),
            Child = new TextBlock
            {
                Text = code,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                FontFamily = SFProBold,
                Foreground = BrushWhite,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = BrushWhite,
            FontFamily = SFProBold,
            Margin = new Thickness(0, 0, 0, 3)
        });
        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = BrushDimWhite,
            FontFamily = SFProBold
        });

        var checkmark = new TextBlock
        {
            Text = "✓",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = BrushWhite,
            FontFamily = SFProBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.5, 0.5),
            Tag = "checkmark"
        };

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        Grid.SetColumn(codeBadge, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(checkmark, 2);
        contentGrid.Children.Add(codeBadge);
        contentGrid.Children.Add(textStack);
        contentGrid.Children.Add(checkmark);

        var border = new Border
        {
            Background = BrushCardBg,
            BorderBrush = BrushCardBorder,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Cursor = Cursors.Hand,
            Tag = langCode,
            Child = contentGrid,
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
            {
                AnimateBorderBackground(border, BrushHoverBg);
            }
        };

        border.MouseLeave += (s, e) =>
        {
            if ((string)border.Tag != _selectedLanguage)
            {
                AnimateBorderBackground(border, BrushCardBg);
            }
        };

        return border;
    }

    private void UpdateSelectionVisuals(bool animate)
    {
        foreach (var kvp in _cards)
        {
            UpdateCardVisual(kvp.Value, kvp.Key == _selectedLanguage, animate);
        }
    }

    private static void UpdateCardVisual(Border card, bool isSelected, bool animate)
    {
        var targetBg = isSelected ? BrushSelectedBg : BrushCardBg;
        var targetBorder = isSelected ? BrushSelectedBorder : BrushCardBorder;

        if (animate)
        {
            var borderAnim = new ColorAnimation(
                ((SolidColorBrush)targetBorder).Color,
                TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var bgAnim = new ColorAnimation(
                ((SolidColorBrush)targetBg).Color,
                TimeSpan.FromMilliseconds(250))
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
                var press = new DoubleAnimation(0.97, TimeSpan.FromMilliseconds(100))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var release = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(300))
                {
                    BeginTime = TimeSpan.FromMilliseconds(100),
                    EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }
                };

                var kf = new DoubleAnimationUsingKeyFrames();
                kf.KeyFrames.Add(new EasingDoubleKeyFrame(0.97, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                kf.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
                    new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }));

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

        if (card.Child is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is TextBlock tb && tb.Tag as string == "checkmark")
                {
                    if (animate)
                    {
                        var targetOpacity = isSelected ? 1.0 : 0.0;
                        var targetScale = isSelected ? 1.0 : 0.5;

                        var fadeAnim = new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(200))
                        {
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                        };
                        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fadeAnim, VNotch.Services.AnimationConfig.TargetFps);
                        tb.BeginAnimation(OpacityProperty, fadeAnim);

                        if (tb.RenderTransform is ScaleTransform checkSt)
                        {
                            var scaleAnim = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(250))
                            {
                                EasingFunction = isSelected
                                    ? new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6 }
                                    : (IEasingFunction)new QuadraticEase { EasingMode = EasingMode.EaseIn }
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

        var anim = new ColorAnimation(targetBrush.Color, TimeSpan.FromMilliseconds(150))
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
