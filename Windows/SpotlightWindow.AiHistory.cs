using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private readonly SpotlightChatStore _chatStore;
    private List<SpotlightSavedChat> _savedChats = new();
    private string _chatId = Guid.NewGuid().ToString("N");
    internal readonly VisualBrush AiTopEdgeBrush = new();
    internal readonly VisualBrush AiBottomEdgeBrush = new();

    private static readonly SolidColorBrush AiHistoryCardIdleBrush = CreateFrozenRgbBrush(18, 18, 18);
    private static readonly SolidColorBrush AiHistoryCardHoverBrush = CreateFrozenRgbBrush(32, 32, 32);
    private static readonly SolidColorBrush AiHistoryCardBorderBrush = CreateFrozenRgbBrush(35, 35, 35);
    private static readonly SolidColorBrush AiHistoryEmptyBrush = CreateFrozenRgbBrush(140, 149, 158);
    private static readonly System.Windows.Media.Animation.QuarticEase AiHistoryCollapseEase = CreateAiHistoryCollapseEase();

    private static SolidColorBrush CreateFrozenRgbBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Animation.QuarticEase CreateAiHistoryCollapseEase()
    {
        var ease = new System.Windows.Media.Animation.QuarticEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        ease.Freeze();
        return ease;
    }

    private SpotlightSavedChat? FindLatestMatchingSavedChat(string provider, string model)
    {
        SpotlightSavedChat? best = null;
        for (int i = 0; i < _savedChats.Count; i++)
        {
            var candidate = _savedChats[i];
            if (candidate.Messages.Count == 0 || candidate.Provider != provider || candidate.Model != model)
                continue;
            if (best == null || candidate.UpdatedUtc > best.UpdatedUtc)
                best = candidate;
        }
        return best;
    }

    private SpotlightSavedChat? FindSavedChatById(string id)
    {
        for (int i = 0; i < _savedChats.Count; i++)
        {
            if (_savedChats[i].Id == id)
                return _savedChats[i];
        }
        return null;
    }

    private string? PopTrailingUnfinishedUserTurn()
    {
        if (_aiHistory.Count > 0 && _aiHistory[^1] is { Role: "user" } unfinished)
        {
            _aiHistory.RemoveAt(_aiHistory.Count - 1);
            return unfinished.Content;
        }
        return null;
    }

    private void LoadAiHistory()
    {
        if (!_settings.SaveAiChatHistory) return;
        _savedChats = _chatStore.Load();
        string provider = _settings.SpotlightAiProvider;
        string model = SpotlightAiService.Configuration(_settings, provider).Model;
        var latest = FindLatestMatchingSavedChat(provider, model);
        if (latest != null)
        {
            _chatId = latest.Id;
            _aiHistory.AddRange(latest.Messages);
            _aiConversationProvider = latest.Provider;
            _aiConversationModel = latest.Model;
            _aiDraftToRestore = PopTrailingUnfinishedUserTurn() ?? latest.Draft;
        }
        RenderAiHistory();
    }
    private string? _aiDraftToRestore;

    private void SaveAiHistory()
    {
        if (!_settings.SaveAiChatHistory) return;
        if (_aiHistory.Count == 0) return;
        var entry = FindSavedChatById(_chatId);
        if (entry == null) { entry = new SpotlightSavedChat { Id = _chatId }; _savedChats.Add(entry); }
        entry.Messages = new List<SpotlightAiMessage>(_aiHistory);
        entry.Provider = _aiConversationProvider;
        entry.Model = _aiConversationModel;
        if (_aiMode) entry.Draft = SearchBox.Text;
        entry.UpdatedUtc = DateTime.UtcNow;
        PersistAiHistoryAsync().SafeFireAndForget("SPOTLIGHT-CHAT");
    }

    private async Task PersistAiHistoryAsync()
    {
        if (!await _chatStore.SaveAsync(_savedChats) && !_allowClose) SetAiStatus("spotlight.ai.saveError");
    }

    internal Task FlushPersistenceAsync() => _chatStore.FlushAsync();

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
        UpdateAiScrollEdges();

        if (showHistory)
        {
            AiHistoryItems.Children.Clear();
            var sfProDisplay = (FontFamily)FindResource("SFProDisplay");
            var toolbarStyle = (Style)FindResource("AiToolbarStyle");
            string deleteLabel = Loc.Get("spotlight.ai.delete");
            var culture = Loc.GetCulture();

            var indexedChats = new (SpotlightSavedChat Chat, int Index)[_savedChats.Count];
            for (int i = 0; i < _savedChats.Count; i++)
                indexedChats[i] = (_savedChats[i], i);
            Array.Sort(indexedChats, static (a, b) =>
            {
                int cmp = b.Chat.UpdatedUtc.CompareTo(a.Chat.UpdatedUtc);
                return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
            });

            for (int idx = 0; idx < indexedChats.Length; idx++)
            {
                var chat = indexedChats[idx].Chat;
                var card = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = AiHistoryCardIdleBrush,
                    BorderBrush = AiHistoryCardBorderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 10, 10),
                    Margin = new Thickness(0, 0, 0, 6),
                    Cursor = Cursors.Hand
                };

                var grid = new Grid { UseLayoutRounding = false };
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
                    FontFamily = sfProDisplay,
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
                    Text = chat.Provider + " · " + chat.UpdatedUtc.ToLocalTime().ToString("g", culture),
                    FontFamily = sfProDisplay,
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
                    Style = toolbarStyle,
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = deleteLabel,
                    Opacity = 1,
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Automation.AutomationProperties.SetName(delete, deleteLabel);
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
                    await PersistAiHistoryAsync();

                    if (!AnimationConfig.ReduceMotion)
                    {
                        var collapseAnim = new System.Windows.Media.Animation.DoubleAnimation(card.ActualHeight, 0, TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = AiHistoryCollapseEase
                        };
                        var fadeAnim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
                        var completed = new TaskCompletionSource<bool>();
                        collapseAnim.Completed += (_, _) => completed.TrySetResult(true);
                        card.BeginAnimation(HeightProperty, collapseAnim);
                        card.BeginAnimation(OpacityProperty, fadeAnim);
                        var spacing = new System.Windows.Media.Animation.ThicknessAnimation(card.Margin, new Thickness(0), TimeSpan.FromMilliseconds(220))
                        {
                            EasingFunction = AiHistoryCollapseEase
                        };
                        card.BeginAnimation(MarginProperty, spacing);
                        card.BeginAnimation(PaddingProperty, new System.Windows.Media.Animation.ThicknessAnimation(
                            card.Padding, new Thickness(0), TimeSpan.FromMilliseconds(220))
                        { EasingFunction = AiHistoryCollapseEase });
                        await completed.Task;
                    }
                    AiHistoryItems.Children.Remove(card);
                    if (AiHistoryItems.Children.Count == 0 && AiHistoryPanel.Visibility == Visibility.Visible)
                    {
                        var empty = new TextBlock
                        {
                            Text = Loc.Get("spotlight.ai.noHistory"),
                            Foreground = UiPalette.IconBrush,
                            FontFamily = sfProDisplay,
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
                card.MouseEnter += (_, _) => card.Background = AiHistoryCardHoverBrush;
                card.MouseLeave += (_, _) => card.Background = AiHistoryCardIdleBrush;

                // Card click to load chat
                card.MouseLeftButtonUp += async (_, args) =>
                {
                    args.Handled = true;
                    if (!card.IsHitTestVisible) return;
                    await TransitionAiPageAsync(() =>
                    {
                        _chatId = chat.Id;
                        _aiHistory.Clear();
                        _aiHistory.AddRange(chat.Messages);
                        _aiConversationProvider = chat.Provider;
                        _aiConversationModel = chat.Model;
                        SearchBox.Text = PopTrailingUnfinishedUserTurn() ?? chat.Draft;
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
                    Foreground = AiHistoryEmptyBrush,
                    FontFamily = sfProDisplay,
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
        UpdateAiScrollEdges();
    }

    internal void ToggleAiHistoryPanel() => ToggleAiHistoryPage();

    internal void ShowAiHistoryPanel()
    {
        if (AiHistoryPanel.Visibility != Visibility.Visible) ToggleAiHistoryPage();
        else UpdateAiScrollEdges();
    }

    internal void HideAiHistoryPanel() => CloseAiHistory();

    internal void UpdateAiScrollEdges()
    {
        if (AiTopEdge == null || AiBottomEdge == null) return;
        if (AiHistoryPanel.Visibility == Visibility.Visible)
        {
            AiTopEdge.Visibility = Visibility.Collapsed;
            AiBottomEdge.Visibility = Visibility.Collapsed;
            return;
        }
        AiTopEdge.Visibility = Visibility.Visible;
        AiBottomEdge.Visibility = Visibility.Visible;
        SetAiEdge(AiTopEdge, AiTranscriptScroll.VerticalOffset > 1, ref _aiTopEdgeVisible);
        SetAiEdge(AiBottomEdge,
            AiTranscriptScroll.ScrollableHeight - AiTranscriptScroll.VerticalOffset > 1,
            ref _aiBottomEdgeVisible);
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
