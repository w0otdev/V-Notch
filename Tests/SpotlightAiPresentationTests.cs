using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightAiPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamingConversationRendersBothTurnsAndResetsToWelcome(bool reducedMotion) => Run(reducedMotion, async ct =>
    {
        string? requestBody = null;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(token);
            return Stream("Hello **world**.\n\n- One\n- Two");
        }));
        using var fixture = new SpotlightWindowFixture(Settings(), new SpotlightAiService(client), saveChatHistory: true);
        var window = fixture.Window;
        window.ShowSpotlight();
        Invoke(window, "ToggleAiMode");
        Assert.Equal(Visibility.Visible, window.AiWelcome.Visibility);
        Assert.False(window.AiSendButton.IsEnabled);
        window.SearchBox.Text = "Explain this";
        Assert.True(window.AiSendButton.IsEnabled);
        await (Task)Invoke(window, "SendAiAsync")!;
        Assert.NotNull(requestBody);
        using var json = JsonDocument.Parse(requestBody!);
        Assert.Equal("Explain this", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        var history = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
        Assert.Equal(2, history.Count);
        Assert.False(history[1].IsIncomplete);
        Assert.Equal("Hello **world**.\n\n- One\n- Two", history[1].Content);
        Assert.Equal(2, window.AiTranscript.Children.Count);
        var view = Descendants<RichTextBox>(window.AiTranscript).Single();
        Assert.Contains("Hello world", new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text);
        Assert.Equal(Visibility.Collapsed, window.AiWelcome.Visibility);
        Assert.True(window.AiCopyButton.IsEnabled);
        Assert.Null(Field<object?>(window, "_aiRequest"));
        Assert.Equal(Visibility.Collapsed, window.AiStopButton.Visibility);
        await WpfFrameWaiter.UntilAsync(() => window.AiUsageRow.Visibility == Visibility.Visible, "stream usage published", ct);
        var store = new SpotlightChatStore(System.IO.Path.Combine(fixture.DirectoryPath, "chats.enc"));
        Assert.Equal(history, store.Load().Single().Messages);
        string originalId = Field<string>(window, "_chatId");
        Invoke(window, "CreateNewAiChat");
        Assert.Empty(history);
        Assert.NotEqual(originalId, Field<string>(window, "_chatId"));
        Assert.Equal(Visibility.Visible, window.AiWelcome.Visibility);
        Assert.Empty(window.SearchBox.Text);
        Invoke(window, "ToggleAiMode");
        Assert.Equal(Visibility.Collapsed, window.AiPanel.Visibility);
        await WpfFrameWaiter.UntilAsync(() => window.AiGlowBorder.Visibility == Visibility.Collapsed, "AI glow dismissed", ct);
    });

    [Theory]
    [InlineData(401, "spotlight.ai.authError")]
    [InlineData(429, "spotlight.ai.rateError")]
    [InlineData(500, "spotlight.ai.serverError")]
    [InlineData(200, "spotlight.ai.emptyError")]
    public void FailedRequestRestoresPromptAndCanRetry(int status, string error) => Run(false, async ct =>
    {
        int attempts = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(++attempts == 1
            ? new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("data: [DONE]\n\n") }
            : Stream("Retry succeeded"))));
        using var fixture = new SpotlightWindowFixture(Settings(), new SpotlightAiService(client));
        var window = fixture.Window;
        window.ShowSpotlight();
        Invoke(window, "ToggleAiMode");
        window.SearchBox.Text = "Retry me";
        await (Task)Invoke(window, "SendAiAsync")!;
        Assert.Empty(Field<List<SpotlightAiMessage>>(window, "_aiHistory"));
        Assert.Equal("Retry me", window.SearchBox.Text);
        Assert.Equal(error, Field<string>(window, "_aiStatusKey"));
        Assert.Equal("error", window.AiStatus.Tag);
        Assert.Equal(Visibility.Visible, window.AiWelcome.Visibility);
        Assert.True(window.AiSendButton.IsEnabled);
        await (Task)Invoke(window, "SendAiAsync")!;
        Assert.Equal(2, attempts);
        Assert.Equal("Retry succeeded", Field<List<SpotlightAiMessage>>(window, "_aiHistory")[1].Content);
        Assert.Null(window.AiStatus.Tag);
        await WpfFrameWaiter.NextAsync(ct);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationStopsBusyIndicatorAndRestoresOnlyPendingPrompt(bool reducedMotion) => Run(reducedMotion, async ct =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return Stream("unreachable");
        }));
        using var fixture = new SpotlightWindowFixture(Settings(), new SpotlightAiService(client));
        var window = fixture.Window;
        window.ShowSpotlight();
        Invoke(window, "ToggleAiMode");
        window.SearchBox.Text = "Pending draft";
        var sending = (Task)Invoke(window, "SendAiAsync")!;
        await started.Task.WaitAsync(ct);
        Assert.Equal(Visibility.Visible, window.AiStopButton.Visibility);
        Assert.False(window.AiSendButton.IsEnabled);
        window.SearchBox.Text = "Next question";
        Assert.Equal(Visibility.Visible, window.AiBottomActionRow.Visibility);
        Assert.Equal(Visibility.Visible, window.AiStopButton.Visibility);
        window.SearchBox.Clear();
        Assert.NotNull(Field<Border?>(window, "_aiThinkingCard"));
        Invoke(window, "ShowAiThinking");
        Assert.Equal(2, window.AiTranscript.Children.Count);
        Invoke(window, "UpdateAiActivity");
        Assert.Equal(!reducedMotion, Field<bool>(window, "_aiActivityAnimating"));
        Invoke(window, "AiStop_Click", window.AiStopButton, new RoutedEventArgs());
        await sending;
        Assert.True(cancelled);
        Assert.Equal("Pending draft", window.SearchBox.Text);
        Assert.Empty(Field<List<SpotlightAiMessage>>(window, "_aiHistory"));
        Assert.Null(Field<Border?>(window, "_aiThinkingCard"));
        Assert.False(Field<bool>(window, "_aiActivityAnimating"));
        Assert.True(Field<bool>(window, "_aiMode"));
        Invoke(window, "EscapeAiMode");
        await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen, "Escape dismisses Spotlight", ct);
        Assert.True(Field<bool>(window, "_aiMode"));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HistoryCardsRestoreCompletedTurnsAndUnfinishedDrafts(bool reducedMotion, bool unfinished) => Run(reducedMotion, async ct =>
    {
        using var fixture = new SpotlightWindowFixture(Settings(), saveChatHistory: true);
        var window = fixture.Window;
        window.ShowSpotlight();
        Invoke(window, "ToggleAiMode");
        Invoke(window, "ToggleAiHistoryPage");
        Assert.IsType<TextBlock>(Assert.Single(window.AiHistoryItems.Children.Cast<UIElement>()));
        Invoke(window, "ToggleAiHistoryPage");
        var chat = new SpotlightSavedChat
        {
            Provider = "OpenAI",
            Model = "saved-model",
            Draft = "Saved draft",
            Messages = [new("user", "First question"), new("assistant", "Saved answer")]
        };
        if (unfinished) chat.Messages.Add(new("user", "Unfinished question"));
        Field<List<SpotlightSavedChat>>(window, "_savedChats").Add(chat);
        Invoke(window, "ToggleAiHistoryPage");
        var card = Assert.IsType<Border>(Assert.Single(window.AiHistoryItems.Children.Cast<UIElement>()));
        Assert.Contains(Descendants<TextBlock>(card), t => t.Text == "First question");
        card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        Assert.Equal(Color.FromRgb(32, 32, 32), Assert.IsType<SolidColorBrush>(card.Background).Color);
        card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        await WpfFrameWaiter.UntilAsync(() => window.AiHistoryPanel.Visibility == Visibility.Collapsed, "saved chat selected", ct);
        Assert.Equal(chat.Id, Field<string>(window, "_chatId"));
        Assert.Equal(2, Field<List<SpotlightAiMessage>>(window, "_aiHistory").Count);
        Assert.Equal(unfinished ? "Unfinished question" : "Saved draft", window.SearchBox.Text);
        Assert.Equal("saved-model", Field<string>(window, "_aiConversationModel"));
        Invoke(window, "ToggleAiHistoryPage");
        card = Assert.IsType<Border>(Assert.Single(window.AiHistoryItems.Children.Cast<UIElement>()));
        Descendants<Button>(card).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WpfFrameWaiter.UntilAsync(() => Field<List<SpotlightSavedChat>>(window, "_savedChats").Count == 0 &&
            window.AiHistoryItems.Children.OfType<TextBlock>().Any(), "deleted chat empty state", ct);
        Assert.Empty(Field<List<SpotlightAiMessage>>(window, "_aiHistory"));
        Assert.Empty(new SpotlightChatStore(System.IO.Path.Combine(fixture.DirectoryPath, "chats.enc")).Load());
    });

    [Theory]
    [InlineData("configure")]
    [InlineData("tooLong")]
    [InlineData("privacyBlocked")]
    [InlineData("networkError")]
    public void InvalidOrBlockedRequestsKeepTranscriptEmpty(string condition) => Run(true, async ct =>
    {
        var settings = Settings();
        if (condition == "configure") settings.SpotlightOpenAIApiKey = "";
        if (condition == "privacyBlocked") settings.EnableLocalOnlyMode = true;
        int requests = 0;
        using var client = new HttpClient(new Handler((_, _) => { requests++; throw new HttpRequestException("test failure"); }));
        using var fixture = new SpotlightWindowFixture(settings, new SpotlightAiService(client));
        var window = fixture.Window;
        Invoke(window, "ToggleAiMode");
        window.SearchBox.Text = condition == "tooLong" ? new string('x', 16001) : "Test question";
        await (Task)Invoke(window, "SendAiAsync")!;
        Assert.Equal("spotlight.ai." + condition, Field<string>(window, "_aiStatusKey"));
        Assert.Empty(Field<List<SpotlightAiMessage>>(window, "_aiHistory"));
        Assert.Equal(condition == "networkError" ? 1 : 0, requests);
        Assert.NotEmpty(window.SearchBox.Text);
        await WpfFrameWaiter.NextAsync(ct);
    });

    [Fact]
    public void ConversationContextDropsIncompleteTurnsAndBoundsRequestWithoutLosingTranscript() => Run(true, async ct =>
    {
        string? requestBody = null;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(token);
            return Stream("Answer");
        }));
        using var fixture = new SpotlightWindowFixture(Settings(), new SpotlightAiService(client));
        var window = fixture.Window;
        Invoke(window, "ToggleAiMode");
        Set(window, "_aiConversationProvider", "OpenAI");
        Set(window, "_aiConversationModel", "test-model");
        var history = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
        for (int i = 0; i < 15; i++)
        {
            history.Add(new("user", new string('q', 4000) + i));
            history.Add(new("assistant", new string('a', 4000) + i) { IsIncomplete = i == 14 });
        }
        Invoke(window, "RenderAiHistory");
        window.SearchBox.Text = "Follow up";
        await (Task)Invoke(window, "SendAiAsync")!;
        using var json = JsonDocument.Parse(requestBody!);
        var messages = json.RootElement.GetProperty("messages");
        Assert.True(messages.GetArrayLength() <= 21);
        Assert.True(messages.EnumerateArray().Sum(m => m.GetProperty("content").GetString()!.Length) <= 64000);
        Assert.DoesNotContain(messages.EnumerateArray(), m => m.GetProperty("content").GetString()!.EndsWith("14", StringComparison.Ordinal));
        Assert.Equal(32, history.Count);
        Assert.Equal("Follow up", history[^2].Content);
        Assert.Equal("Answer", history[^1].Content);
        await WpfFrameWaiter.NextAsync(ct);
    });

    [Theory]
    [InlineData(false, "hotkey")]
    [InlineData(true, "hotkey")]
    [InlineData(false, "escape")]
    [InlineData(true, "escape")]
    [InlineData(false, "deactivate")]
    [InlineData(true, "deactivate")]
    public void DismissedRequestCompletesAndReopeningPreservesItsTranscript(bool reopenBeforeCompletion, string dismissal) => Run(true, async ct =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken requestToken = default;
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            requestToken = token;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Stream("Completed in the background");
        }));
        var settings = Settings();
        settings.SpotlightDefaultAi = false;
        using var fixture = new SpotlightWindowFixture(settings, new SpotlightAiService(client), saveChatHistory: true);
        var window = fixture.Window;
        window.ShowSpotlight();
        Invoke(window, "ToggleAiMode");
        window.SearchBox.Text = "Keep working";
        var sending = (Task)Invoke(window, "SendAiAsync")!;
        await started.Task.WaitAsync(ct);
        var thinking = Field<Border?>(window, "_aiThinkingCard");
        if (dismissal == "escape") window.HandleGlobalEscape();
        else if (dismissal == "deactivate") Invoke(window, "Window_Deactivated", window, EventArgs.Empty);
        else window.ToggleFromHotkey();
        await WpfFrameWaiter.UntilAsync(() => !window.IsSpotlightOpen, "Spotlight dismissed", ct);
        Assert.False(requestToken.IsCancellationRequested);
        Assert.NotNull(Field<object?>(window, "_aiRequest"));
        if (reopenBeforeCompletion)
        {
            window.ShowSpotlight();
            Assert.True(Field<bool>(window, "_aiMode"));
            Assert.Contains(thinking!, window.AiTranscript.Children.Cast<UIElement>());
            Assert.Equal(Visibility.Visible, window.AiStopButton.Visibility);
            Assert.False(requestToken.IsCancellationRequested);
        }
        release.TrySetResult();
        await sending.WaitAsync(ct);
        if (!reopenBeforeCompletion)
        {
            Assert.False(window.IsSpotlightOpen);
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
        }
        var history = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
        Assert.Equal(2, history.Count);
        Assert.Equal("Keep working", history[0].Content);
        Assert.Equal("Completed in the background", history[1].Content);
        Assert.False(history[1].IsIncomplete);
        Assert.Contains("Completed in the background", new TextRange(
            Descendants<RichTextBox>(window.AiTranscript).Single().Document.ContentStart,
            Descendants<RichTextBox>(window.AiTranscript).Single().Document.ContentEnd).Text);
        Assert.Null(Field<object?>(window, "_aiRequest"));
        Assert.Equal(history, new SpotlightChatStore(System.IO.Path.Combine(fixture.DirectoryPath, "chats.enc")).Load().Single().Messages);
    });

    private static NotchSettings Settings()
    {
        var settings = new NotchSettings { SpotlightAiProvider = "OpenAI" };
        SpotlightAiService.Configure(settings, "OpenAI", "test-secret", "test-model");
        return settings;
    }

    private static HttpResponseMessage Stream(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text } } },
            usage = new { total_tokens = 12 }
        }) + "\n\ndata: [DONE]\n\n")
    };

    private static void Run(bool reducedMotion, Func<CancellationToken, Task> action) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        try { AnimationConfig.SetReduceMotion(reducedMotion); await action(ct); }
        finally { AnimationConfig.SetReduceMotion(previous); }
    }, timeoutSeconds: 90);

    private static T Field<T>(SpotlightWindow window, string name) => (T)typeof(SpotlightWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(SpotlightWindow window, string name, object value) => typeof(SpotlightWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(SpotlightWindow window, string name, params object?[] args) => typeof(SpotlightWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T match) yield return match;
            if (child is DependencyObject dependency)
                foreach (var descendant in Descendants<T>(dependency)) yield return descendant;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
