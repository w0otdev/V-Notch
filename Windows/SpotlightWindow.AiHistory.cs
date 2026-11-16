using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private readonly IChatRepository _chatStore;
    private readonly VNotch.ApplicationServices.ChatSession _chatSession;
    private List<ChatSummary> _savedChats = new();
    private string _chatId = Guid.NewGuid().ToString("N");
    private int _messageOffset;
    private int _transcriptPageOffset;
    private int _historyPageOffset;
    private int _historyTotal;
    private int _historyGeneration;
    private int _transcriptGeneration;
    private Task _historyLoadTask = Task.CompletedTask;
    private Task _historyPageTask = Task.CompletedTask;
    private bool _loadingTranscript;
    private string? _aiDraftToRestore;
    internal readonly VisualBrush AiTopEdgeBrush = new();
    internal readonly VisualBrush AiBottomEdgeBrush = new();
    private static readonly SolidColorBrush AiHistoryCardIdleBrush = Controls.SpotlightChatPalette.Surface;
    private static readonly SolidColorBrush AiHistoryCardHoverBrush = Controls.SpotlightChatPalette.Hover;
    private static readonly SolidColorBrush AiHistoryCardBorderBrush = Controls.SpotlightChatPalette.Border;

    private string? PopTrailingUnfinishedUserTurn()
    {
        if (_aiHistory.Count > 0 && _aiHistory[^1] is { Role: "user" } unfinished)
        {
            _aiHistory.RemoveAt(_aiHistory.Count - 1);
            return unfinished.Content;
        }
        return null;
    }

    private void LoadAiHistory() => _historyLoadTask = LoadAiHistoryAsync();

    private async Task LoadAiHistoryAsync()
    {
        if (!_settings.SaveAiChatHistory) return;
        string originalId = _chatId;
        string provider = _settings.SpotlightAiProvider;
        string model = SpotlightAiService.Configuration(_settings, provider).Model;
        try
        {
            var latest = await _chatStore.LatestAsync(provider, model);
            var chat = latest == null ? null : await _chatStore.LoadAsync(latest.Id);
            if (_allowClose || !_settings.SaveAiChatHistory || _chatId != originalId || _aiHistory.Count != 0 ||
                provider != _settings.SpotlightAiProvider || model != SpotlightAiService.Configuration(_settings, provider).Model) return;
            if (chat != null)
            {
                RestoreChat(chat);
                _aiDraftToRestore = PopTrailingUnfinishedUserTurn() ?? chat.Draft;
                if (_aiMode && string.IsNullOrEmpty(SearchBox.Text)) { SearchBox.Text = _aiDraftToRestore; _aiDraftToRestore = null; }
            }
            RenderAiHistory();
        }
        catch (Exception ex) { ReportChatFailure(ex); }
    }

    private void RestoreChat(SpotlightSavedChat chat)
    {
        _chatId = chat.Id;
        _messageOffset = chat.MessageOffset;
        _aiHistory.Clear();
        _aiHistory.AddRange(chat.Messages);
        _aiConversationProvider = chat.Provider;
        _aiConversationModel = chat.Model;
    }

    private void SaveAiHistory()
    {
        if (!_settings.SaveAiChatHistory || _aiHistory.Count == 0) return;
        var chat = new SpotlightSavedChat
        {
            Id = _chatId,
            Messages = new List<SpotlightAiMessage>(_aiHistory),
            MessageOffset = _messageOffset,
            Provider = _aiConversationProvider,
            Model = _aiConversationModel,
            Draft = _aiMode ? SearchBox.Text : _aiDraftToRestore ?? "",
            UpdatedUtc = DateTime.UtcNow
        };
        ObserveChatSaveAsync(_chatSession.SaveAsync(chat), chat).SafeFireAndForget("SPOTLIGHT-CHAT");
    }

    private async Task ObserveChatSaveAsync(Task save, SpotlightSavedChat saved)
    {
        try
        {
            await save;
            if (_allowClose || !_settings.SaveAiChatHistory || _chatId != saved.Id || _messageOffset != saved.MessageOffset) return;
            int trim = Math.Min(saved.Messages.Count, Math.Max(0, _aiHistory.Count - 32));
            // Evict only a persisted prefix that still belongs to the current conversation.
            if (trim > 0 && _aiHistory.Take(trim).SequenceEqual(saved.Messages.Take(trim)))
            {
                _aiHistory.RemoveRange(0, trim);
                _messageOffset += trim;
            }
        }
        catch (Exception ex) { ReportChatFailure(ex); }
    }

    private void ReportChatFailure(Exception error)
    {
        RuntimeLog.Warn("SPOTLIGHT-CHAT", $"Chat storage failed ({error.GetType().Name}); original data retained.");
        if (!_allowClose) SetAiStatus("spotlight.ai.saveError");
    }

    internal async Task FlushPersistenceAsync()
    {
        await _chatSession.FlushAsync().ConfigureAwait(false);
        await _chatStore.FlushAsync().ConfigureAwait(false);
    }

    private async void AiHistory_Click(object sender, RoutedEventArgs e) => await TransitionAiPageAsync(ToggleAiHistoryPage);

    private void ToggleAiHistoryPage()
    {
        CancelAiRequest();
        SaveAiHistory();
        bool showHistory = AiHistoryPanel.Visibility != Visibility.Visible;
        AiWelcome.Visibility = Visibility.Collapsed;
        AiHistoryPanel.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        AiTranscriptScroll.Visibility = showHistory ? Visibility.Collapsed : Visibility.Visible;
        UpdateAiScrollEdges();
        if (showHistory) _historyPageTask = LoadHistoryPageAsync(0);
        else { CloseAiHistory(); RefreshAiPanel(); }
        ScheduleContentResize();
    }

    private async Task LoadHistoryPageAsync(int offset)
    {
        int generation = ++_historyGeneration;
        try
        {
            await _chatSession.FlushAsync();
            var page = await _chatStore.ListAsync(offset, 40);
            if (_allowClose || generation != _historyGeneration || !_settings.SaveAiChatHistory) return;
            if (offset > 0 && page.Items.Count == 0) { await LoadHistoryPageAsync(Math.Max(0, offset - 40)); return; }
            _historyPageOffset = offset;
            _historyTotal = page.TotalCount;
            _savedChats = page.Items.ToList();
            AiHistoryItems.CreateCard = CreateHistoryCard;
            AiHistoryItems.ItemsSource = _savedChats;
            AiHistoryCount.Text = page.TotalCount.ToString(Loc.GetCulture());
            AiHistoryEmpty.Text = Loc.Get("spotlight.ai.noHistory");
            AiHistoryEmpty.Visibility = page.TotalCount == 0 ? Visibility.Visible : Visibility.Collapsed;
            AiHistoryPrevious.IsEnabled = offset > 0;
            AiHistoryNext.IsEnabled = offset + page.Items.Count < page.TotalCount;
            ScheduleContentResize();
        }
        catch (Exception ex) { ReportChatFailure(ex); }
    }

    private void AiHistoryPrevious_Click(object sender, RoutedEventArgs e) => _historyPageTask = LoadHistoryPageAsync(Math.Max(0, _historyPageOffset - 40));
    private void AiHistoryNext_Click(object sender, RoutedEventArgs e) => _historyPageTask = LoadHistoryPageAsync(_historyPageOffset + 40);

    private FrameworkElement CreateHistoryCard(ChatSummary chat)
    {
        var sfProDisplay = (FontFamily)FindResource("SFProDisplay");
        var toolbarStyle = (Style)FindResource("AiToolbarStyle");
        string deleteLabel = Loc.Get("spotlight.ai.delete");
        var culture = Loc.GetCulture();
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = AiHistoryCardIdleBrush,
            BorderBrush = chat.Id == _chatId ? AiHistoryCardBorderBrush : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 6, 4, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand
        };

        var grid = new Grid { UseLayoutRounding = false };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var currentMark = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 9, 0, 9),
            Visibility = chat.Id == _chatId ? Visibility.Visible : Visibility.Hidden
        };
        currentMark.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        grid.Children.Add(currentMark);

        var openContent = new Grid();
        openContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        openContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = CreateAiBrandImage(chat.Provider, 18);
        openContent.Children.Add(icon);

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
            FontSize = 13,
            Foreground = Controls.SpotlightChatPalette.Primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = chat.Title
        };
        var subtitleBlock = new TextBlock
        {
            Text = SpotlightAiModelName.Format(chat.Model, chat.Provider) + " · " + chat.UpdatedUtc.ToLocalTime().ToString("g", culture),
            ToolTip = chat.Provider + " · " + chat.Model,
            FontFamily = sfProDisplay,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Controls.SpotlightChatPalette.Secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0)
        };
        label.Children.Add(titleBlock);
        string preview = chat.Preview;
        if (preview.Length > 0)
        {
            label.Children.Add(new TextBlock
            {
                Text = preview,
                FontFamily = sfProDisplay,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Controls.SpotlightChatPalette.Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        label.Children.Add(subtitleBlock);
        openContent.Children.Add(label);
        var open = new Button
        {
            Style = (Style)FindResource("AiHistoryOpenStyle"),
            Content = openContent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        System.Windows.Automation.AutomationProperties.SetName(open, chat.Title);
        Grid.SetColumn(open, 1);
        grid.Children.Add(open);

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
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = deleteLabel,
            Opacity = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(delete, deleteLabel);
        Grid.SetColumn(delete, 2);

        delete.Click += async (_, args) =>
        {
            args.Handled = true;
            if (!_settings.SaveAiChatHistory || !card.IsHitTestVisible) return;
            card.IsHitTestVisible = false;
            try
            {
                await _chatSession.FlushAsync();
                await _chatStore.DeleteAsync(chat.Id);
                if (chat.Id == _chatId)
                {
                    _aiHistory.Clear(); _messageOffset = 0; _chatId = Guid.NewGuid().ToString("N");
                    SearchBox.Clear(); RenderAiHistory();
                }
                await LoadHistoryPageAsync(_historyPageOffset);
            }
            catch (Exception ex) { card.IsHitTestVisible = true; ReportChatFailure(ex); }
        };
        grid.Children.Add(delete);
        card.Child = grid;
        card.MouseEnter += (_, _) => card.Background = AiHistoryCardHoverBrush;
        card.MouseLeave += (_, _) => card.Background = AiHistoryCardIdleBrush;
        async Task OpenChat()
        {
            if (!card.IsHitTestVisible) return;
            card.IsHitTestVisible = false;
            int generation = _historyGeneration;
            try
            {
                var saved = await _chatStore.LoadAsync(chat.Id);
                if (saved == null || _allowClose || generation != _historyGeneration || !_settings.SaveAiChatHistory) return;
                await TransitionAiPageAsync(() =>
                {
                    RestoreChat(saved);
                    SearchBox.Text = PopTrailingUnfinishedUserTurn() ?? saved.Draft;
                    CloseAiHistory(); RenderAiHistory(); RefreshAiPanel(); SetAiStatus("spotlight.ai.hint"); SearchBox.Focus();
                });
            }
            catch (Exception ex) { ReportChatFailure(ex); }
            finally { card.IsHitTestVisible = true; }
        }
        open.Click += async (_, args) => { args.Handled = true; await OpenChat(); };
        card.MouseLeftButtonUp += async (_, args) => { args.Handled = true; await OpenChat(); };
        return card;
    }

    private async Task LoadTranscriptPageAsync(int before)
    {
        if (_loadingTranscript || _aiRequest != null) return;
        if (before > _messageOffset)
        {
            int end = Math.Min(before - _messageOffset, _aiHistory.Count);
            int start = Math.Max(0, end - 32);
            RenderTranscriptPage(_aiHistory.Skip(start).Take(end - start).ToArray(), _messageOffset + start);
            return;
        }
        if (!_settings.SaveAiChatHistory) return;
        _loadingTranscript = true;
        string id = _chatId;
        int generation = _transcriptGeneration;
        try
        {
            await _chatSession.FlushAsync();
            var page = await _chatStore.LoadAsync(id, before);
            if (page == null || _allowClose || _chatId != id || !_settings.SaveAiChatHistory ||
                _aiRequest != null || generation != _transcriptGeneration) return;
            RenderTranscriptPage(page.Messages, page.MessageOffset);
        }
        catch (Exception ex) { ReportChatFailure(ex); }
        finally { _loadingTranscript = false; }
    }

    private void AiOlderMessages_Click(object sender, RoutedEventArgs e) => LoadTranscriptPageAsync(_transcriptPageOffset).SafeFireAndForget("SPOTLIGHT-CHAT");
    private void AiLatestMessages_Click(object sender, RoutedEventArgs e) => RenderAiHistory();

    private void CloseAiHistory()
    {
        ++_historyGeneration;
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
        // A dark scroll overlay belongs to the opaque skin. Over live glass it
        // paints two rectangular bars across the captured backdrop.
        if (IsLiquidGlassEnabled || AiHistoryPanel.Visibility == Visibility.Visible)
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
        bool showWelcome = _aiHistory.Count == 0 && AiHistoryPanel.Visibility != Visibility.Visible;
        AiWelcome.Visibility = showWelcome ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AiSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string prompt }) return;
        SearchBox.Text = prompt;
        SearchBox.Focus();
        SearchBox.CaretIndex = prompt.Length;
        AnimateAiPromptReveal();
    }
}
