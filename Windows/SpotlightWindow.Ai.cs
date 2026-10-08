using System.Windows;
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

    internal static bool IsAiToggle(Key key, ModifierKeys modifiers) => key == Key.Tab && modifiers == ModifierKeys.None;

    private bool HandleAiKey(KeyEventArgs e)
    {
        if (IsAiToggle(e.Key, Keyboard.Modifiers))
        {
            if (!e.IsRepeat) ToggleAiMode();
            e.Handled = true;
            return true;
        }
        if (!_aiMode) return false;
        // Keep all search launch/navigation shortcuts out of the AI surface.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (!e.IsRepeat) _ = SendAiAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EscapeAiMode();
            e.Handled = true;
        }
        return true;
    }

    private void EscapeAiMode()
    {
        DismissFromGlobalShortcut();
    }

    private void ToggleAiMode()
    {
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
        AiModeLabel.Text = Loc.Get("spotlight.ai.agent", _aiHistory.Count > 0 ? _aiConversationProvider : _settings.SpotlightAiProvider);
        SetAiToolLabel(AiNewChatButton, "spotlight.ai.newChat");
        SetAiToolLabel(AiBackButton, "spotlight.ai.back");
        SetAiToolLabel(AiCopyButton, "spotlight.ai.copy");
        AiHistoryButton.ToolTip = Loc.Get("spotlight.ai.history");
        System.Windows.Automation.AutomationProperties.SetName(AiHistoryButton, Loc.Get("spotlight.ai.history"));
        AiWelcomeTitle.Text = Loc.Get("spotlight.ai.welcome");
        AiWelcomeHint.Text = Loc.Get("spotlight.ai.welcomeHint");
        AiPromptOne.Content = Loc.Get("spotlight.ai.promptOne");
        AiPromptTwo.Content = Loc.Get("spotlight.ai.promptTwo");
        AiStopButton.ToolTip = Loc.Get("spotlight.ai.stop");
        System.Windows.Automation.AutomationProperties.SetName(AiStopButton, Loc.Get("spotlight.ai.stop"));
        RenderAiStatus();
    }

    private void SetAiStatus(string key, params object[] args)
    {
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
        ResultsList.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Collapsed;
        AiPanel.Visibility = Visibility.Visible;
        SetStatusPulse(false);
        UpdateAiActivity();
        UpdateAiActionState();
        UpdateAiUsage();
        bool shown = _contentShown;
        SetContentShown(true);
        if (shown) ScheduleContentResize();
        UpdateAiWelcome();
        AiModeLabel.Text = Loc.Get("spotlight.ai.agent", _aiHistory.Count > 0 ? _aiConversationProvider : _settings.SpotlightAiProvider);
        SetEscBadgeVisible(true);
        EscBadgeText.Text = "TAB · " + Loc.Get("spotlight.searchMode");
    }

    private void RenderAiHistory()
    {
        UpdateAiWelcome();
        UpdateAiMetadata();
        AiTranscript.Children.Clear();
        foreach (var message in _aiHistory) AddAiMessage(message, animate: false);
        AiTranscriptScroll.ScrollToEnd();
    }

    private System.Windows.Controls.RichTextBox CreateMarkdownView(string text)
    {
        var font = (System.Windows.Media.FontFamily?)TryFindResource("SFProDisplay")
            ?? new System.Windows.Media.FontFamily("pack://application:,,,/V-Notch;component/Fonts/#SF Pro Display, Segoe UI, Arial");
        return new System.Windows.Controls.RichTextBox
        {
            Document = VNotch.Controls.AiMarkdown.Render(text, font),
            IsReadOnly = true,
            IsDocumentEnabled = false,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = font,
            FontWeight = FontWeights.Bold,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        };
    }

    private async Task SendAiAsync()
    {
        if (!_aiMode || _aiRequest != null || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        CloseAiHistory();
        var settings = _settings.Clone();
        if (_aiHistory.Count > 0)
        {
            settings.SpotlightAiProvider = _aiConversationProvider;
            var savedConfig = SpotlightAiService.Configuration(settings, _aiConversationProvider);
            SpotlightAiService.Configure(settings, _aiConversationProvider, savedConfig.Key, _aiConversationModel);
        }
        var config = SpotlightAiService.Configuration(settings, settings.SpotlightAiProvider);
        if (!SpotlightAiService.IsConfigured(settings))
        {
            SetAiStatus("spotlight.ai.configure");
            RefreshAiPanel();
            return;
        }
        if (_aiConversationProvider != settings.SpotlightAiProvider || _aiConversationModel != config.Model)
        {
            _aiHistory.Clear();
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
        using var cts = new CancellationTokenSource();
        _aiRequest = cts;
        RefreshAiPanel();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var progressTimer = new System.Windows.Threading.DispatcherTimer
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
        int revealedLength = 0;
        bool revealCaughtUp = false;
        AiUsageSnapshot? receivedUsage = null;
        var revealTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(110) };
        void RevealText(bool finish = false)
        {
            if (!ReferenceEquals(_aiRequest, cts) || liveView == null) return;
            revealTimer.Interval = TimeSpan.FromMilliseconds(1000d / Math.Clamp(_settings.SpotlightAiWordsPerSecond, 2, 30));
            bool follow = AiTranscriptScroll.ScrollableHeight - AiTranscriptScroll.VerticalOffset < 32;
            if (finish || AnimationConfig.ReduceMotion)
            {
                liveView.Document = VNotch.Controls.AiMarkdown.Render(receivedText);
                revealCaughtUp = true;
            }
            else
            {
                var doc = VNotch.Controls.AiMarkdown.RenderWords(receivedText, revealedLength + 1,
                    out int shown, out revealCaughtUp, Math.Min(240, revealTimer.Interval.TotalMilliseconds));
                if (shown == revealedLength) return;
                revealedLength = shown;
                liveView.Document = doc;
            }
            if (follow) AiTranscriptScroll.ScrollToEnd();
            ScheduleContentResize();
        }
        revealTimer.Tick += (_, _) => RevealText();
        revealTimer.Start();
        try
        {
            // Interrupted responses stay visible but are not replayed as completed turns.
            var history = new List<SpotlightAiMessage>();
            int i = 0;
            while (i < _aiHistory.Count)
            {
                if (i + 1 < _aiHistory.Count && _aiHistory[i + 1].IsIncomplete)
                {
                    i += 2;
                    continue;
                }
                history.Add(_aiHistory[i]);
                i++;
            }
            while (history.Count > 21 || (history.Count > 2 && history.Sum(m => m.Content.Length) > 64000))
                history.RemoveRange(0, 2);
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
                }
                _aiHistory[^1] = new SpotlightAiMessage(AssistantRole, response.ToString()) { IsIncomplete = true };
                receivedText = response.ToString();
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
            revealTimer.Stop();
            RevealText(finish: true);
            if (ReferenceEquals(_aiRequest, cts) && liveView != null)
                liveView.Document = VNotch.Controls.AiMarkdown.Render(receivedText);
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
                SaveAiHistory();
                if (liveView == null) RenderAiHistory();
                else { UpdateAiWelcome(); UpdateAiMetadata(); }
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
        cts.Cancel();
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
        CloseAiHistory();
        _aiHistory.Clear();
        RenderAiHistory();
        SearchBox.Clear();
        SetAiStatus("spotlight.ai.hint");
        RefreshAiPanel();
        SearchBox.Focus();
    }
}
