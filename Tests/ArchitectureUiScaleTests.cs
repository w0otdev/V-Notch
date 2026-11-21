using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ArchitectureUiScaleTests
{
    [Theory]
    [Trait("Category", "ArchitectureScale")]
    [InlineData(100)]
    [InlineData(1000)]
    public void HistoryRealizesOnlyVisibleCardsAndLoadsOneTranscriptPage(int count) => SharedStaTestRunner.RunAsync(async token =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            var repository = new PagedRepository(count);
            var settings = new NotchSettings { SpotlightAiProvider = "OpenAI" };
            SpotlightAiService.Configure(settings, "OpenAI", "test-key", "test");
            using var fixture = new SpotlightWindowFixture(settings, saveChatHistory: true, chatStore: repository);
            var window = fixture.Window;
            await Field<Task>(window, "_historyLoadTask");
            Assert.Equal(32, Field<List<SpotlightAiMessage>>(window, "_aiHistory").Count);
            Assert.Equal(32, repository.LargestLoad);
            window.ShowSpotlight();
            Invoke(window, "ToggleAiMode");
            var layoutTimes = new List<double>();
            for (int sample = 0; sample < 20; sample++)
            {
                window.HideAiHistoryPanel();
                long started = Stopwatch.GetTimestamp();
                window.ShowAiHistoryPanel();
                await Field<Task>(window, "_historyPageTask");
                window.UpdateLayout();
                await WpfFrameWaiter.UntilAsync(() => window.AiHistoryItems.ItemContainerGenerator.ContainerFromIndex(0) != null, "paged cards realized", token);
                layoutTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            double uiP95Milliseconds = layoutTimes.Order().ElementAt(18);
            Assert.Equal(40, window.AiHistoryItems.Items.Count);
            int realized = Enumerable.Range(0, 40).Count(i => window.AiHistoryItems.ItemContainerGenerator.ContainerFromIndex(i) != null);
            Assert.InRange(realized, 1, 15);
            Assert.NotNull(window.AiHistoryItems.CardStop(39, false));
            Assert.NotNull(window.AiHistoryItems.ItemContainerGenerator.ContainerFromIndex(39));
            Assert.InRange(Enumerable.Range(0, 40).Count(i => window.AiHistoryItems.ItemContainerGenerator.ContainerFromIndex(i) != null), 1, 15);
            window.AiHistoryNext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Field<Task>(window, "_historyPageTask");
            Assert.Equal(40, repository.LastOffset);
            window.HideAiHistoryPanel();
            await (Task)Invoke(window, "LoadTranscriptPageAsync", count - 32)!;
            Assert.Equal(count - 64, Field<int>(window, "_transcriptPageOffset"));

            string json = JsonSerializer.Serialize(new { count, samples = layoutTimes.Count, uiP95Milliseconds, realizedCards = realized, conversationPageSize = 40,
                transcriptPageSize = repository.LargestLoad, limitations = "WPF layout with reduced motion and synthetic repository; excludes GPU composition, animations and disk loading." },
                new JsonSerializerOptions { WriteIndented = true });
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "V-Notch.sln"))) directory = directory.Parent;
            string results = Environment.GetEnvironmentVariable("VNOTCH_SCALE_RESULTS") ?? Path.Combine(directory!.FullName, "artifacts", "architecture-scale");
            Directory.CreateDirectory(results);
            await File.WriteAllTextAsync(Path.Combine(results, $"ui-{count}.json"), json, token);
        }
        finally { AnimationConfig.SetReduceMotion(reduced); }
    });

    [Fact]
    public void UnsavedConversationCanPageOlderMessages() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new SpotlightWindowFixture(new NotchSettings());
        var window = fixture.Window;
        Invoke(window, "ToggleAiMode");
        var messages = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
        messages.AddRange(Enumerable.Range(0, 80).Select(i => new SpotlightAiMessage(i % 2 == 0 ? "user" : "assistant", $"message {i}")));
        Invoke(window, "RenderAiHistory");
        Assert.Equal(48, Field<int>(window, "_transcriptPageOffset"));
        Assert.Equal(Visibility.Visible, window.AiOlderMessages.Visibility);
        await (Task)Invoke(window, "LoadTranscriptPageAsync", 48)!;
        Assert.Equal(16, Field<int>(window, "_transcriptPageOffset"));
        Assert.Equal(80, messages.Count);
    });

    [Fact]
    public void LateOlderPageCannotReplaceLatestTranscript() => SharedStaTestRunner.RunAsync(async () =>
    {
        var repository = new PagedRepository(100);
        using var fixture = new SpotlightWindowFixture(new NotchSettings(), saveChatHistory: true, chatStore: repository);
        var window = fixture.Window;
        await Field<Task>(window, "_historyLoadTask");
        repository.ReadBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task older = (Task)Invoke(window, "LoadTranscriptPageAsync", 68)!;
        Assert.False(older.IsCompleted);
        Invoke(window, "RenderAiHistory");
        repository.ReadBarrier.SetResult();
        await older;
        Assert.Equal(68, Field<int>(window, "_transcriptPageOffset"));
    });

    [Fact]
    public void LateHistoryOpenCannotReplaceANewConversation() => SharedStaTestRunner.RunAsync(async () =>
    {
        var repository = new PagedRepository(100);
        using var fixture = new SpotlightWindowFixture(new NotchSettings(), saveChatHistory: true, chatStore: repository);
        var window = fixture.Window;
        await Field<Task>(window, "_historyLoadTask");
        window.ShowSpotlight();
        Invoke(window, "ToggleAiMode");
        window.ShowAiHistoryPanel();
        await Field<Task>(window, "_historyPageTask");
        var summary = Assert.Single((await repository.ListAsync(0, 1)).Items);
        var card = (Border)Invoke(window, "CreateHistoryCard", summary)!;
        var open = ((Grid)card.Child).Children.OfType<Button>().Single(button => Grid.GetColumn(button) == 1);
        repository.ReadBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(card.IsHitTestVisible);
        Invoke(window, "CreateNewAiChat");
        string id = Field<string>(window, "_chatId");
        repository.ReadBarrier.SetResult();
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.True(card.IsHitTestVisible);
        Assert.Equal(id, Field<string>(window, "_chatId"));
        Assert.Empty(Field<List<SpotlightAiMessage>>(window, "_aiHistory"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlySuccessfullyPersistedMessagesLeaveMemory(bool failSave) => SharedStaTestRunner.RunAsync(async () =>
    {
        var repository = new PagedRepository(100) { FailSave = failSave };
        using var fixture = new SpotlightWindowFixture(new NotchSettings(), saveChatHistory: true, chatStore: repository);
        var window = fixture.Window;
        await Field<Task>(window, "_historyLoadTask");
        var messages = Field<List<SpotlightAiMessage>>(window, "_aiHistory");
        messages.AddRange(Enumerable.Range(100, 68).Select(i => new SpotlightAiMessage(i % 2 == 0 ? "user" : "assistant", $"message {i}")));
        Invoke(window, "SaveAiHistory");
        var flush = Field<VNotch.ApplicationServices.ChatSession>(window, "_chatSession").FlushAsync();
        if (failSave) await Assert.ThrowsAsync<AggregateException>(() => flush);
        else await flush;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.Equal(failSave ? 100 : 32, messages.Count);
        Assert.Equal(failSave ? 68 : 136, Field<int>(window, "_messageOffset"));
        if (failSave)
        {
            repository.FailSave = false;
            Invoke(window, "SaveAiHistory");
            await Field<VNotch.ApplicationServices.ChatSession>(window, "_chatSession").FlushAsync();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(32, messages.Count);
            Assert.Equal(136, Field<int>(window, "_messageOffset"));
        }
    });

    private static T Field<T>(SpotlightWindow window, string name) =>
        (T)typeof(SpotlightWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(SpotlightWindow window, string name, params object[] arguments) =>
        typeof(SpotlightWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);

    private sealed class PagedRepository(int count) : IChatRepository
    {
        private readonly ChatSummary[] _summaries = Enumerable.Range(0, count).Select(i =>
            new ChatSummary(Guid.NewGuid().ToString("N"), "OpenAI", "test", $"Conversation {i}", "Synthetic preview", DateTime.UtcNow, count)).ToArray();
        public int LargestLoad { get; private set; }
        public int LastOffset { get; private set; }
        public TaskCompletionSource? ReadBarrier { get; set; }
        public bool FailSave { get; set; }
        public Task<ChatPage> ListAsync(int offset, int pageSize, CancellationToken cancellation = default)
        { LastOffset = offset; return Task.FromResult(new ChatPage(_summaries.Skip(offset).Take(pageSize).ToArray(), count)); }
        public Task<ChatSummary?> LatestAsync(string provider, string model, CancellationToken cancellation = default) => Task.FromResult<ChatSummary?>(_summaries[0]);
        public async Task<SpotlightSavedChat?> LoadAsync(string id, int before = int.MaxValue, int pageSize = 32, CancellationToken cancellation = default)
        {
            if (ReadBarrier is { } barrier) await barrier.Task;
            LargestLoad = Math.Max(LargestLoad, pageSize);
            int end = Math.Min(before, count), start = Math.Max(0, end - pageSize);
            return new()
            {
                Id = id, Provider = "OpenAI", Model = "test", MessageOffset = start, TotalMessages = count,
                Messages = Enumerable.Range(start, end - start).Select(i => new SpotlightAiMessage(i % 2 == 0 ? "user" : "assistant", $"Message {i}")).ToList()
            };
        }
        public Task SaveChatAsync(SpotlightSavedChat chat, CancellationToken cancellation = default) =>
            FailSave ? Task.FromException(new IOException("Simulated write failure")) : Task.CompletedTask;
        public Task DeleteAsync(string id, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task FlushAsync() => Task.CompletedTask;
    }
}
