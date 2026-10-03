using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private readonly SpotlightChatStore _chatStore = new();
    private List<SpotlightSavedChat> _savedChats = new();
    private string _chatId = Guid.NewGuid().ToString("N");

    private void LoadAiHistory()
    {
        if (!_settings.SaveAiChatHistory) return;
        _savedChats = _chatStore.Load();
        var latest = _savedChats.OrderByDescending(c => c.UpdatedUtc).FirstOrDefault(c => c.Messages.Count > 0
            && c.Provider == _settings.SpotlightAiProvider
            && c.Model == SpotlightAiService.Configuration(_settings, _settings.SpotlightAiProvider).Model);
        if (latest != null)
        {
            _chatId = latest.Id;
            _aiHistory.AddRange(latest.Messages);
            _aiConversationProvider = latest.Provider;
            _aiConversationModel = latest.Model;
            _aiDraftToRestore = latest.Draft;
            if (_aiHistory.LastOrDefault() is { Role: "user" } unfinished)
            {
                _aiDraftToRestore = unfinished.Content;
                _aiHistory.RemoveAt(_aiHistory.Count - 1);
            }
        }
        RenderAiHistory();
    }
    private string? _aiDraftToRestore;

    private void SaveAiHistory()
    {
        if (!_settings.SaveAiChatHistory) return;
        if (_aiHistory.Count == 0) return;
        var entry = _savedChats.FirstOrDefault(c => c.Id == _chatId);
        if (entry == null) { entry = new SpotlightSavedChat { Id = _chatId }; _savedChats.Add(entry); }
        entry.Messages = _aiHistory.ToList();
        entry.Provider = _aiConversationProvider;
        entry.Model = _aiConversationModel;
        if (_aiMode) entry.Draft = SearchBox.Text;
        entry.UpdatedUtc = DateTime.UtcNow;
        if (!_chatStore.Save(_savedChats)) SetAiStatus("spotlight.ai.saveError");
    }

    private async void AiHistory_Click(object sender, RoutedEventArgs e)
    {
        await TransitionAiPageAsync(ToggleAiHistoryPage);
    }

    private void ToggleAiHistoryPage()
    {
        CancelAiRequest();
        SaveAiHistory();
        bool showHistory = AiHistoryPanel.Visibility != Visibility.Visible;
        AiWelcome.Visibility = Visibility.Collapsed;
        AiHistoryPanel.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        AiTranscriptScroll.Visibility = showHistory ? Visibility.Collapsed : Visibility.Visible;

        if (showHistory)
        {
            AiHistoryItems.Children.Clear();
            var chats = _savedChats.OrderByDescending(c => c.UpdatedUtc).ToArray();

            foreach (var chat in chats)
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 10, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand
                };

                var grid = new Grid { UseLayoutRounding = true };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // Chat icon
                var icon = new System.Windows.Shapes.Path
                {
                    Data = AiChatGeometry,
                    Fill = ChatIconBrush,
                    Stretch = Stretch.Uniform,
                    Width = 13,
                    Height = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                grid.Children.Add(icon);

                // Title + Subtitle
                var label = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                Grid.SetColumn(label, 1);
                var titleBlock = new TextBlock
                {
                    Text = chat.Title,
                    FontFamily = (FontFamily)FindResource("SFProDisplay"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12.5,
                    Foreground = UiPalette.PrimaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    TextAlignment = TextAlignment.Left,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ToolTip = chat.Title
                };
                var subtitleBlock = new TextBlock
                {
                    Text = chat.Provider + " · " + chat.UpdatedUtc.ToLocalTime().ToString("g", Loc.GetCulture()),
                    FontFamily = (FontFamily)FindResource("SFProDisplay"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = UiPalette.IconBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    TextAlignment = TextAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                label.Children.Add(titleBlock);
                label.Children.Add(subtitleBlock);
                grid.Children.Add(label);

                // Delete button
                var delete = new Button
                {
                    Content = new System.Windows.Shapes.Path
                    {
                        Data = AiBinGeometry,
                        Fill = ChatIconBrush,
                        Stretch = Stretch.Uniform,
                        Width = 12,
                        Height = 12
                    },
                    Style = (Style)FindResource("AiToolbarStyle"),
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = Loc.Get("spotlight.ai.delete"),
                    Opacity = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Automation.AutomationProperties.SetName(delete, Loc.Get("spotlight.ai.delete"));
                Grid.SetColumn(delete, 2);



                delete.Click += async (s, args) =>
                {
                    args.Handled = true;
                    if (!_settings.SaveAiChatHistory) return;
                    if (!card.IsHitTestVisible) return;
                    card.IsHitTestVisible = false;
                    card.ClipToBounds = true;
                    _savedChats.Remove(chat);
                    if (chat.Id == _chatId) { _aiHistory.Clear(); _chatId = Guid.NewGuid().ToString("N"); SearchBox.Clear(); RenderAiHistory(); }
                    if (!_chatStore.Save(_savedChats)) SetAiStatus("spotlight.ai.saveError");

                    if (!AnimationConfig.ReduceMotion)
                    {
                        var collapseAnim = new System.Windows.Media.Animation.DoubleAnimation(card.ActualHeight, 0, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                        };
                        var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
                        var completed = new TaskCompletionSource<bool>();
                        collapseAnim.Completed += (_, _) => completed.TrySetResult(true);
                        card.BeginAnimation(HeightProperty, collapseAnim);
                        card.BeginAnimation(OpacityProperty, fadeAnim);
                        var spacing = new System.Windows.Media.Animation.ThicknessAnimation(card.Margin, new Thickness(0), TimeSpan.FromMilliseconds(220));
                        spacing.EasingFunction = collapseAnim.EasingFunction;
                        card.BeginAnimation(MarginProperty, spacing);
                        card.BeginAnimation(PaddingProperty, new System.Windows.Media.Animation.ThicknessAnimation(
                            card.Padding, new Thickness(0), TimeSpan.FromMilliseconds(220))
                        { EasingFunction = collapseAnim.EasingFunction });
                        await completed.Task;
                    }
                    AiHistoryItems.Children.Remove(card);
                    if (AiHistoryItems.Children.Count == 0 && AiHistoryPanel.Visibility == Visibility.Visible)
                    {
                        var empty = new TextBlock
                        {
                            Text = Loc.Get("spotlight.ai.noHistory"),
                            Foreground = UiPalette.IconBrush,
                            FontFamily = (FontFamily)FindResource("SFProDisplay"),
                            FontWeight = FontWeights.Bold,
                            Margin = new Thickness(12, 16, 12, 16),
                            HorizontalAlignment = HorizontalAlignment.Center
                        };
                        AiHistoryItems.Children.Add(empty);
                        AnimateAiArrival(empty, 4, 200);
                    }
                    ScheduleContentResize();
                };
                grid.Children.Add(delete);

                card.Child = grid;

                // Card hover animation
                card.MouseEnter += (_, _) =>
                {
                    card.Background = new SolidColorBrush(Color.FromRgb(32, 32, 32));
                };
                card.MouseLeave += (_, _) =>
                {
                    card.Background = new SolidColorBrush(Color.FromRgb(18, 18, 18));
                };

                // Card click to load chat
                card.MouseLeftButtonUp += async (_, args) =>
                {
                    args.Handled = true;
                    if (!card.IsHitTestVisible) return;
                    await TransitionAiPageAsync(() =>
                    {
                        _chatId = chat.Id;
                        _aiHistory.Clear(); _aiHistory.AddRange(chat.Messages);
                        _aiConversationProvider = chat.Provider; _aiConversationModel = chat.Model;
                        SearchBox.Text = chat.Draft;
                        if (_aiHistory.LastOrDefault() is { Role: "user" } unfinished)
                        {
                            SearchBox.Text = unfinished.Content;
                            _aiHistory.RemoveAt(_aiHistory.Count - 1);
                        }
                        CloseAiHistory();
                        RenderAiHistory();
                        RefreshAiPanel();
                        SetAiStatus("spotlight.ai.hint");

                        SearchBox.Focus();
                    });
                };

                AiHistoryItems.Children.Add(card);

            }

            if (AiHistoryItems.Children.Count == 0)
            {
                AiHistoryItems.Children.Add(new TextBlock
                {
                    Text = Loc.Get("spotlight.ai.noHistory"),
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 149, 158)),
                    FontFamily = (FontFamily)FindResource("SFProDisplay"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Margin = new Thickness(12, 16, 12, 16),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }

        }
        else
        {
            CloseAiHistory();
            RefreshAiPanel();

        }
        ScheduleContentResize();
    }
    private void CloseAiHistory()
    {
        AiHistoryPanel.Visibility = Visibility.Collapsed;
        AiTranscriptScroll.Visibility = Visibility.Visible;
    }
    private void UpdateAiWelcome()
    {
        bool wasVisible = AiWelcome.Visibility == Visibility.Visible;
        bool showWelcome = _aiHistory.Count == 0 && AiHistoryPanel.Visibility != Visibility.Visible;
        AiWelcome.Visibility = showWelcome ? Visibility.Visible : Visibility.Collapsed;
        // A failed first request restores this whole surface, not just the status line.
        // Only reveal on entry so normal metadata/input refreshes do not restart it.
        if (showWelcome && !wasVisible && _aiMode && IsSpotlightOpen && !_isClosing)
        {
            AnimateAiFade(AiHeader);
            AnimateAiFade(AiWelcome);
        }
    }
    private void AiSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string prompt }) { SearchBox.Text = prompt; SearchBox.Focus(); SearchBox.CaretIndex = prompt.Length; }
    }
}
