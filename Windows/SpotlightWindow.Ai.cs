using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private const string AssistantRole = "assistant";
    private static readonly TimeSpan AiRequestTimeout = TimeSpan.FromSeconds(60);
    private bool _aiMode;
    private readonly List<SpotlightAiMessage> _aiHistory = new();
    private readonly SpotlightAiService _aiService;
    private CancellationTokenSource? _aiRequest;
    private string _aiConversationProvider = "";
    private string _aiConversationModel = "";
    private string _aiStatusKey = "spotlight.ai.hint";
    private object[] _aiStatusArgs = [];
    private string? _aiStatusDiagnostic;

    internal static bool IsAiToggle(Key key, ModifierKeys modifiers, bool searchBoxFocused = true) =>
        key == Key.Tab && modifiers == ModifierKeys.None && searchBoxFocused;

    internal bool IsAiToggle(KeyEventArgs e) =>
        IsAiToggle(e.Key, Keyboard.Modifiers, IsSearchBoxFocusedForAiToggle(e));

    private bool IsSearchBoxFocusedForAiToggle(KeyEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && source != this && source != SearchBox && !SearchBox.IsAncestorOf(source))
            return false;
        if (Keyboard.FocusedElement is DependencyObject kb && GetWindow(kb) == this)
            return kb == SearchBox || SearchBox.IsAncestorOf(kb);
        var logical = FocusManager.GetFocusedElement(this) as DependencyObject;
        if (logical != null)
            return logical == SearchBox || SearchBox.IsAncestorOf(logical);
        return !_aiMode || SearchBox.IsKeyboardFocused;
    }

    private bool HandleAiKey(KeyEventArgs e)
    {
        if (_aiProviderMenu is { IsOpen: true })
        {
            if (e.Key == Key.Escape)
            {
                CloseAiProviderMenu();
                e.Handled = true;
            }
            return true;
        }
        if (IsAiToggle(e))
        {
            if (!e.IsRepeat) ToggleAiMode();
            e.Handled = true;
            return true;
        }
        if (!_aiMode) return false;
        if (e.Key == Key.Tab && (Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift))
        {
            AdvanceAiTabFocus(Keyboard.Modifiers == ModifierKeys.Shift, e);
            e.Handled = true;
            return true;
        }
        // Keep all search launch/navigation shortcuts out of the AI surface.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && IsSearchBoxFocusedForAiToggle(e))
        {
            if (!e.IsRepeat) SendAiAsync().SafeFireAndForget("SPOTLIGHT-AI");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EscapeAiMode();
            e.Handled = true;
        }
        return true;
    }

    private void AdvanceAiTabFocus(bool reverse, KeyEventArgs e)
    {
        var current = (Keyboard.FocusedElement as DependencyObject is { } kb && GetWindow(kb) == this ? kb : null)
            ?? FocusManager.GetFocusedElement(this) as DependencyObject
            ?? e.OriginalSource as DependencyObject;
        if (AiHistoryPanel.Visibility == Visibility.Visible)
        {
            var historyTarget = current == AiNewChatButton && !reverse
                ? AiHistoryItems.CardStop(0, false)
                : AiHistoryItems.NextCardStop(current, reverse);
            if (historyTarget != null)
            {
                FocusManager.SetFocusedElement(this, historyTarget);
                historyTarget.Focus();
                return;
            }
        }
        var stopList = new List<Control>(8);
        void AddIfFocusable(Control? control, bool parentVisible = true)
        {
            if (parentVisible && control is { Visibility: Visibility.Visible, IsEnabled: true, Focusable: true })
                stopList.Add(control);
        }

        AddIfFocusable(AiBackButton);
        AddIfFocusable(AiProviderButton);
        AddIfFocusable(AiCopyButton);
        AddIfFocusable(AiHistoryButton);
        AddIfFocusable(AiNewChatButton);
        bool welcomeVisible = AiWelcome?.Visibility == Visibility.Visible;
        AddIfFocusable(AiPromptOne, welcomeVisible);
        AddIfFocusable(AiPromptTwo, welcomeVisible);
        if (AiHistoryPanel?.Visibility == Visibility.Visible && AiHistoryItems != null)
        {
            AddTranscriptStops(AiHistoryItems);
            AddIfFocusable(AiHistoryPrevious);
            AddIfFocusable(AiHistoryNext);
        }
        AddIfFocusable(AiOlderMessages);
        AddIfFocusable(AiLatestMessages);
        if (AiTranscriptScroll.Visibility == Visibility.Visible)
            AddTranscriptStops(AiTranscript);
        AddIfFocusable(AiUsageRefresh, AiUsageRow?.Visibility == Visibility.Visible);
        AddIfFocusable(AiStopButton, AiBottomActionRow?.Visibility == Visibility.Visible);
        AddIfFocusable(SearchBox);

        if (stopList.Count == 0) return;

        int index = stopList.FindIndex(c => c == current || (current != null && c.IsAncestorOf(current)));
        int nextIndex = index < 0
            ? (reverse ? stopList.Count - 1 : 0)
            : (index + (reverse ? stopList.Count - 1 : 1)) % stopList.Count;

        Control target = stopList[nextIndex];
        FocusManager.SetFocusedElement(this, target);
        target.Focus();

        void AddTranscriptStops(DependencyObject parent)
        {
            if (parent is System.Windows.Controls.ItemsControl items)
            {
                for (int i = 0; i < items.Items.Count; i++)
                    if (items.ItemContainerGenerator.ContainerFromIndex(i) is System.Windows.Controls.ContentControl { Content: DependencyObject content }) AddTranscriptStops(content);
                return;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is Button button) AddIfFocusable(button);
                else if (child is RichTextBox view) AddIfFocusable(view);
                else if (child is DependencyObject nested) AddTranscriptStops(nested);
            }
        }
    }

    private void EscapeAiMode()
    {
        DismissFromGlobalShortcut();
    }

    private void ToggleAiMode()
    {
        CloseAiProviderMenu();
        CancelAiRequest();
        CancelSearchDebounce();
        CancelSearchingGrace();
        _pendingLaunchQuery = null;
        _viewModel.CancelPendingSearch();
        ClearLaunchFailure();
        _aiMode = !_aiMode;
        if (_aiMode && _aiDraftToRestore != null) { SearchBox.Text = _aiDraftToRestore; _aiDraftToRestore = null; }
        AiPanel.Visibility = _aiMode ? Visibility.Visible : Visibility.Collapsed;
        AutocompleteText.Visibility = Visibility.Collapsed;
        LocalizeAi();
        AnimateSearchIconToAi(_aiMode);
        UpdateAiAmbientGlow(_aiMode);
        UpdateGlowingCaret();
        if (_aiMode)
        {
            EscBadgeText.Text = "TAB · " + Loc.Get("spotlight.searchMode");
            EscBadge.ToolTip = Loc.Get("spotlight.ai.back");
            RefreshAiPanel();
            AnimateAiArrival(AiPanel, 8, 350);
        }
        else
        {
            EscBadgeText.Text = "TAB · AI";
            EscBadge.ToolTip = null;
            StopAiActivity();
            _ = _viewModel.SearchAsync(SearchBox.Text);
            AnimateAiArrival(ResultsList, 6, 240);
        }
        RefreshStatus();
        SearchBox.Focus();
    }

    private void LocalizeAi()
    {
        EscBadgeText.Text = "TAB · " + (_aiMode ? Loc.Get("spotlight.searchMode") : "AI");
        PlaceholderText.Text = Loc.Get(_aiMode ? "spotlight.ai.placeholder" : "spotlight.placeholder");
        System.Windows.Automation.AutomationProperties.SetName(SearchBox, PlaceholderText.Text);
        UpdateAiIdentity();
        SetAiToolLabel(AiNewChatButton, "spotlight.ai.newChat");
        SetAiToolLabel(AiBackButton, "spotlight.ai.back");
        SetAiToolLabel(AiCopyButton, "spotlight.ai.copy");
        AiHistoryButton.ToolTip = Loc.Get("spotlight.ai.history");
        System.Windows.Automation.AutomationProperties.SetName(AiHistoryButton, Loc.Get("spotlight.ai.history"));
        AiWelcomeTitle.Text = Loc.Get("spotlight.ai.newChat");
        AiHistoryCount.Text = _historyTotal.ToString(Loc.GetCulture());
        AiHistoryPrevious.Content = Loc.Get("spotlight.ai.previousPage");
        AiHistoryNext.Content = Loc.Get("spotlight.ai.nextPage");
        AiOlderMessages.Content = Loc.Get("spotlight.ai.olderMessages");
        AiLatestMessages.Content = Loc.Get("spotlight.ai.latestMessages");
        AiWelcomeHint.Text = Loc.Get("spotlight.ai.welcomeHint");
        AiPromptOne.Content = Loc.Get("spotlight.ai.promptOne");
        AiPromptTwo.Content = Loc.Get("spotlight.ai.promptTwo");
        AiStopButton.ToolTip = Loc.Get("spotlight.ai.stop");
        System.Windows.Automation.AutomationProperties.SetName(AiStopButton, Loc.Get("spotlight.ai.stop"));
        RenderAiStatus();
    }

    private void SetAiStatus(string key, params object[] args)
    {
        if (_aiStatusKey == key && _aiStatusArgs.Length == 0 && args.Length == 0 && _aiStatusDiagnostic == null)
            return;
        _aiStatusKey = key;
        _aiStatusArgs = args;
        _aiStatusDiagnostic = null;
        RenderAiStatus();
        // Animate new error events only, not localization or diagnostic refreshes.
        if (_aiMode && AiStatus.Tag is "error")
        {
            PlayShake();
            AnimateAiFade(AiStatus);
        }
        else
        {
            AiStatus.BeginAnimation(OpacityProperty, null);
            AiStatus.Opacity = 1;
            if (AiStatus.RenderTransform is System.Windows.Media.TranslateTransform translate)
            {
                translate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                translate.Y = 0;
            }
        }
    }

    private void RenderAiStatus()
    {
        string text = Loc.Get(_aiStatusKey, _aiStatusArgs) +
            (_aiStatusDiagnostic is { } diagnostic ? $" ({diagnostic})" : "");
        AiStatus.Tag = _aiStatusKey.EndsWith("Error", StringComparison.Ordinal) ||
            _aiStatusKey is "spotlight.ai.configure" or "spotlight.ai.tooLong" or
                "spotlight.ai.requestTimeout" or "spotlight.ai.timeout" ? "error" : null;
        AiStatus.Text = text;
        AiStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        if (AiStatus.Visibility == Visibility.Visible && AiBottomActionRow.Visibility != Visibility.Visible)
        {
            AiBottomActionRow.Visibility = Visibility.Visible;
        }
        if (_aiMode) ScheduleContentResize();
    }

    private void UpdateAiActionState()
    {
        UpdateAiUsage();
        AiStopButton.SetShown(_aiRequest != null);
        // Reserve the footer so typing or clearing the draft cannot resize the AI view.
        if (AiBottomActionRow.Visibility != Visibility.Visible)
        {
            AiBottomActionRow.Visibility = Visibility.Visible;
            ScheduleContentResize();
        }
    }

    private void RefreshAiPanel()
    {
        RestoreAiTranscript();
        ResultsList.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        AiPanel.Visibility = Visibility.Visible;
        SetSearchActivity(false);
        UpdateAiActivity();
        UpdateAiActionState();
        UpdateAiUsage();
        bool shown = _contentShown;
        SetContentShown(true);
        if (shown) ScheduleContentResize();
        UpdateAiWelcome();
        UpdateAiScrollEdges();
        UpdateAiIdentity();
        SetEscBadgeVisible(true);
        EscBadgeText.Text = "TAB · " + Loc.Get("spotlight.searchMode");
    }

    private void RenderAiHistory()
    {
        UpdateAiIdentity();
        UpdateAiWelcome();
        UpdateAiMetadata();
        RenderTranscriptPage(_aiHistory.TakeLast(32).ToArray(), _messageOffset + Math.Max(0, _aiHistory.Count - 32));
    }

    private void RenderTranscriptPage(IReadOnlyList<SpotlightAiMessage> messages, int offset)
    {
        _renderedAiMessages = messages as SpotlightAiMessage[] ?? messages.ToArray();
        _aiTranscriptReleased = false;
        ++_transcriptGeneration;
        _transcriptPageOffset = offset;
        AiTranscript.Children.Clear();
        foreach (var message in messages) AddAiMessage(message, animate: false);
        AiOlderMessages.Visibility = offset > 0 && (_settings.SaveAiChatHistory || offset > _messageOffset)
            ? Visibility.Visible : Visibility.Collapsed;
        AiLatestMessages.Visibility = offset + messages.Count < _messageOffset + _aiHistory.Count ? Visibility.Visible : Visibility.Collapsed;
        AiTranscriptScroll.ScrollToEnd();
        if (_isParked) ScheduleAiTranscriptRelease();
    }

    private System.Windows.Controls.RichTextBox CreateMarkdownView(string text)
    {
        var font = (System.Windows.Media.FontFamily?)TryFindResource("SFProDisplay")
            ?? new System.Windows.Media.FontFamily(VNotch.Services.AppFonts.Location + "#SF Pro Display, Segoe UI, Arial");
        return new System.Windows.Controls.RichTextBox
        {
            Document = VNotch.Controls.AiMarkdown.Render(text, font, uniformBold: true),
            IsReadOnly = true,
            IsDocumentEnabled = false,
            Background = System.Windows.Media.Brushes.Transparent,
            SelectionBrush = SearchBox.SelectionBrush,
            SelectionTextBrush = SearchBox.SelectionTextBrush,
            SelectionOpacity = SearchBox.SelectionOpacity,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = font,
            FontWeight = FontWeights.Bold,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        };
    }

    private async Task SendAiAsync()
    {
        await _historyLoadTask;
        if (!_aiMode || _aiRequest != null || string.IsNullOrWhiteSpace(SearchBox.Text) || _allowClose) return;
        CloseAiHistory();
        RenderAiHistory();
        var settings = CreateAiRequestSettings();
        var config = SpotlightAiService.Configuration(settings, settings.SpotlightAiProvider);
        if (!SpotlightAiService.IsConfigured(settings))
        {
            SetAiStatus("spotlight.ai.configure");
            RefreshAiPanel();
            return;
        }
        if (_aiConversationProvider != settings.SpotlightAiProvider || _aiConversationModel != config.Model)
        {
            SaveAiHistory();
            _aiHistory.Clear();
            _messageOffset = 0;
            _chatId = Guid.NewGuid().ToString("N");
            _aiConversationProvider = settings.SpotlightAiProvider;
            _aiConversationModel = config.Model;
        }
        string prompt = SearchBox.Text.Trim();
        if (prompt.Length > 16000)
        {
            SetAiStatus("spotlight.ai.tooLong");
            return;
        }
        // Keep complete conversation turns and bound the request size.
        // Keep the full transcript on disk; only the API context is bounded below.
        var pending = new SpotlightAiMessage("user", prompt);
        _aiHistory.Add(pending);
        AddAiMessage(pending, animate: true);
        SearchBox.Clear();
        UpdateAiActionState();
        UpdateAiWelcome();
        UpdateAiMetadata();
        ShowAiThinking();
        using var cts = _chatSession.BeginRequest();
        _aiRequest = cts;
        RefreshAiPanel();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var progressTimer = new UiScheduledTimer
        { Interval = TimeSpan.FromSeconds(1) };
        progressTimer.Tick += (_, _) =>
        {
            if (ReferenceEquals(_aiRequest, cts) && _aiHistory.LastOrDefault()?.Role != AssistantRole)
                SetAiStatus("spotlight.ai.waiting", (int)started.Elapsed.TotalSeconds);
        };
        SetAiStatus("spotlight.ai.waiting", 0);
        progressTimer.Start();
        System.Windows.Controls.RichTextBox? liveView = null;
        string receivedText = "";
        var streamRenderer = new VNotch.Controls.AiMarkdown.StreamRenderer(uniformBold: true);
        bool revealCaughtUp = false;
        AiUsageSnapshot? receivedUsage = null;
        var revealTimer = new UiScheduledTimer
        { Interval = TimeSpan.FromMilliseconds(110) };
        void RevealText(bool finish = false)
        {
            if (!ReferenceEquals(_aiRequest, cts) || liveView == null) return;
            revealTimer.Interval = TimeSpan.FromMilliseconds(1000d / Math.Clamp(_settings.SpotlightAiWordsPerSecond, 2, 30));
            bool follow = AiTranscriptScroll.ScrollableHeight - AiTranscriptScroll.VerticalOffset < 32;
            if (finish || AnimationConfig.ReduceMotion)
            {
                liveView.Document = VNotch.Controls.AiMarkdown.Render(receivedText, uniformBold: true);
                revealCaughtUp = true;
            }
            else
            {
                int step = Math.Max(1, receivedText.Length / 120);
                revealCaughtUp = streamRenderer.Advance(receivedText, step, Math.Min(240, revealTimer.Interval.TotalMilliseconds));
                if (!ReferenceEquals(liveView.Document, streamRenderer.Document)) liveView.Document = streamRenderer.Document;
            }
            if (follow) AiTranscriptScroll.ScrollToEnd();
            ScheduleContentResize();
        }
        revealTimer.Tick += (_, _) => RevealText();
        revealTimer.Start();
        try
        {
            var history = VNotch.ApplicationServices.ChatSession.RequestContext(_aiHistory);
            var geminiParts = new List<System.Text.Json.JsonElement>();
            var response = new System.Text.StringBuilder();
            cts.CancelAfter(AiRequestTimeout);
            await foreach (string delta in _aiService.StreamAsync(settings, history, cts.Token, parts => geminiParts.AddRange(parts), usage =>
            {
                receivedUsage = usage;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_aiRequest, cts)) return;
                    _usageSnapshot = usage;
                    _usageIdentity = UsageIdentity(settings);
                    UpdateAiUsage();
                });
            }))
            {
                if (!ReferenceEquals(_aiRequest, cts) || cts.IsCancellationRequested) return;
                cts.CancelAfter(AiRequestTimeout); // Timeout means no new text, not total generation time.
                response.Append(delta);
                if (liveView == null)
                {
                    HideAiThinking();
                    _aiHistory.Add(new SpotlightAiMessage(AssistantRole, "") { IsIncomplete = true });
                    liveView = AddAiMessage(new SpotlightAiMessage(AssistantRole, ""), animate: true);
                    UpdateAiActivity();
                }
                string currentResponse = response.ToString();
                _aiHistory[^1] = new SpotlightAiMessage(AssistantRole, currentResponse) { IsIncomplete = true };
                receivedText = currentResponse;
                revealCaughtUp = false;
                if (AnimationConfig.ReduceMotion) RevealText(finish: true);
                UpdateAiMetadata();
                SetAiStatus("spotlight.ai.streaming");
            }
            cts.CancelAfter(Timeout.InfiniteTimeSpan);
            while (!revealCaughtUp && ReferenceEquals(_aiRequest, cts))
                await Task.Delay(32, cts.Token);
            if (!ReferenceEquals(_aiRequest, cts) || cts.IsCancellationRequested) return;
            if (_aiHistory.LastOrDefault()?.Role == AssistantRole)
                _aiHistory[^1] = _aiHistory[^1] with
                {
                    IsIncomplete = false,
                    GeminiParts = settings.SpotlightAiProvider == "Gemini" ? geminiParts.ToArray() : null
                };
            RuntimeLog.Debug("SPOTLIGHT-AI", $"stream completed; elapsedMs={started.ElapsedMilliseconds}");
            SetAiStatus("spotlight.ai.hint");
        }
        catch (Exception ex)
        {
            HideAiThinking();
            if (!ReferenceEquals(_aiRequest, cts)) return;
            _ = cts.CancelAsync();
            RuntimeLog.Warn("SPOTLIGHT-AI", $"request ended after {started.Elapsed.TotalSeconds:F0}s ({ex.GetType().Name}; {(ex as SpotlightAiException)?.Diagnostic ?? "no provider status"})");
            // Failed/cancelled requests are retryable without duplicate user turns.
            if (_aiHistory.LastOrDefault()?.Role == "user")
            {
                _aiHistory.Remove(pending);
                if (string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = prompt;
            }
            string statusKey;
            if (ex is SpotlightAiException)
            {
                statusKey = ex.Message;
            }
            else if (ex is TimeoutException or OperationCanceledException)
            {
                statusKey = "spotlight.ai.requestTimeout";
            }
            else
            {
                statusKey = "spotlight.ai.networkError";
            }
            SetAiStatus(statusKey);
            _aiStatusDiagnostic = (ex as SpotlightAiException)?.Diagnostic;
            RenderAiStatus();
        }
        finally
        {
            HideAiThinking();
            progressTimer.Stop();
            progressTimer.Dispose();
            revealTimer.Stop();
            revealTimer.Dispose();
            RevealText(finish: true);
            if (ReferenceEquals(_aiRequest, cts))
            {
                // A buffered response can finish before queued usage callbacks run.
                // Preserve its final usage while this request still owns the UI.
                if (receivedUsage != null)
                {
                    _usageSnapshot = receivedUsage;
                    _usageIdentity = UsageIdentity(settings);
                    UpdateAiUsage();
                }
                _aiRequest = null;
                _chatSession.EndRequest(cts);
                SaveAiHistory();
                if (_settings.SaveAiChatHistory && _aiHistory.Count > 64)
                {
                    string savedId = _chatId;
                    try
                    {
                        await _chatSession.FlushAsync();
                        if (_chatId == savedId && _aiRequest == null)
                        {
                            int trim = Math.Max(0, _aiHistory.Count - 32);
                            _aiHistory.RemoveRange(0, trim); _messageOffset += trim;
                        }
                    }
                    catch (Exception ex) { ReportChatFailure(ex); }
                }
                RenderAiHistory();
                if (_aiMode) RefreshAiPanel();
            }
        }
    }

    private void CancelAiRequest()
    {
        HideAiThinking();
        var cts = _aiRequest;
        if (cts == null) return;
        _aiRequest = null;
        _chatSession.CancelRequest();
        if (_aiHistory.LastOrDefault() is { Role: "user" } pending)
        {
            _aiHistory.RemoveAt(_aiHistory.Count - 1);
            if (string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = pending.Content;
        }
        SaveAiHistory();
        SetAiStatus("spotlight.ai.cancelled");
        RenderAiHistory();
        if (_aiMode) RefreshAiPanel();
    }

    private void AiToggle_Click(object sender, MouseButtonEventArgs e)
    {
        ToggleAiMode();
        e.Handled = true;
    }

    private void AiStop_Click(object sender, RoutedEventArgs e) => CancelAiRequest();
    private void AiStopButton_HideCompleted(object sender, EventArgs e)
    {
        if (IsLoaded && _aiMode && !_isClosing) UpdateAiActionState();
    }
    private async void AiNewChat_Click(object sender, RoutedEventArgs e)
    {
        await TransitionAiPageAsync(CreateNewAiChat);
    }

    private void CreateNewAiChat()
    {
        CancelAiRequest();
        SaveAiHistory();
        _chatId = Guid.NewGuid().ToString("N");
        _messageOffset = 0;
        CloseAiHistory();
        _aiHistory.Clear();
        RenderAiHistory();
        SearchBox.Clear();
        SetAiStatus("spotlight.ai.hint");
        RefreshAiPanel();
        SearchBox.Focus();
    }
}
