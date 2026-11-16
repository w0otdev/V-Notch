using System.IO;
using VNotch.ApplicationServices;
using VNotch.Infrastructure.Windows;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class ArchitecturePersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vnotch-architecture-" + Guid.NewGuid().ToString("N"));
    private EncryptedChatRepository Repository(Func<IReadOnlyList<SpotlightSavedChat>>? legacy = null) =>
        new(_root, DataProtection.Protect, DataProtection.Unprotect, legacy ?? (() => []));
    private static SpotlightSavedChat Chat(int count = 80) => new()
    {
        Provider = "test", Model = "model", Draft = "private draft",
        Messages = Enumerable.Range(0, count).Select(i => new SpotlightAiMessage(i % 2 == 0 ? "user" : "assistant", $"private message {i}")).ToList()
    };

    [Fact]
    public async Task MigrationPagesAndTailWritesPreserveEarlierMessagesAndOtherChats()
    {
        var original = Chat();
        var other = Chat(4);
        int migrations = 0;
        var repository = Repository(() => { migrations++; return [original, other]; });
        var summaries = await repository.ListAsync(0, 1);
        Assert.Single(summaries.Items);
        Assert.Equal(2, summaries.TotalCount);
        string otherIndex = Path.Combine(_root, other.Id, "index.enc");
        byte[] otherBytes = File.ReadAllBytes(otherIndex);
        var tail = (await repository.LoadAsync(original.Id))!;
        Assert.Equal(48, tail.MessageOffset);
        Assert.Equal(32, tail.Messages.Count);
        tail.Messages.Add(new("user", "new turn"));
        await repository.SaveChatAsync(tail);
        Assert.Equal(otherBytes, File.ReadAllBytes(otherIndex));
        var reopened = Repository(() => throw new Exception("Migration must not repeat"));
        var full = (await reopened.LoadAsync(original.Id, count: 100))!;
        Assert.Equal(original.Messages.Concat([new SpotlightAiMessage("user", "new turn")]), full.Messages);
        Assert.Equal(81, full.TotalMessages);
        Assert.Equal(1, migrations);
        Assert.All(Directory.EnumerateFiles(_root, "*.enc", SearchOption.AllDirectories), file =>
            Assert.DoesNotContain("private", File.ReadAllText(file)));
        await reopened.DeleteAsync(other.Id);
        Assert.Null(await Repository().LoadAsync(other.Id));
    }

    [Fact]
    public async Task FailedMigrationRetriesAndDoesNotCommitAnEmptyStore()
    {
        int attempts = 0;
        var chat = Chat(2);
        var repository = Repository(() => ++attempts == 1 ? throw new IOException("Locked legacy file") : [chat]);
        await Assert.ThrowsAsync<IOException>(() => repository.ListAsync(0, 10));
        Assert.False(File.Exists(Path.Combine(_root, "ready")));
        Assert.Equal(chat.Id, Assert.Single((await repository.ListAsync(0, 10)).Items).Id);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CancelledQueuedReadCannotBypassAnActiveWrite()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var repository = Repository(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return [];
        });
        var first = repository.SaveChatAsync(Chat(2));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        using var cancellation = new CancellationTokenSource();
        var cancelled = repository.ListAsync(0, 10, cancellation.Token);
        cancellation.Cancel();
        var next = repository.SaveChatAsync(Chat(2));
        try { Assert.False(next.IsCompleted); }
        finally { release.Set(); }
        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await next;
        Assert.Equal(2, (await repository.ListAsync(0, 10)).TotalCount);
    }

    [Fact]
    public async Task CorruptManifestRecoversPreviousCommittedGenerationAndPreservesEvidence()
    {
        var chat = Chat(2);
        var repository = Repository();
        await repository.SaveChatAsync(chat);
        chat.Messages.Add(new("user", "later"));
        await repository.SaveChatAsync(chat);
        string index = Path.Combine(_root, chat.Id, "index.enc");
        File.WriteAllText(index, DataProtection.Protect("invalid json"));
        var recovered = (await Repository().LoadAsync(chat.Id))!;
        Assert.Equal(2, recovered.Messages.Count);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(index)!, "*.corrupt"));
        Assert.Equal(2, (await Repository().LoadAsync(chat.Id))!.Messages.Count);
    }

    [Fact]
    public async Task LockedManifestIsNotReplacedWithBackup()
    {
        var chat = Chat(2);
        await Repository().SaveChatAsync(chat);
        string index = Path.Combine(_root, chat.Id, "index.enc");
        byte[] original = File.ReadAllBytes(index);
        var reader = Repository();
        using (var handle = new FileStream(index, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => reader.ListAsync(0, 10));
        Assert.Equal(original, File.ReadAllBytes(index));
        Assert.Single((await reader.ListAsync(0, 10)).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrecoverableChatDoesNotBlockHealthyChatsOrDisappearAfterFlush(bool corruptBackup)
    {
        var damaged = Chat(2);
        var healthy = Chat(4);
        var writer = Repository();
        await writer.SaveChatAsync(damaged);
        await writer.SaveChatAsync(healthy);
        string index = Path.Combine(_root, damaged.Id, "index.enc");
        File.WriteAllText(index, DataProtection.Protect("invalid json"));
        if (corruptBackup) File.WriteAllText(index + ".bak", DataProtection.Protect("also invalid"));
        byte[] evidence = File.ReadAllBytes(index);

        var reader = Repository();
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var page = await reader.ListAsync(0, 10);
            Assert.Equal(healthy.Id, Assert.Single(page.Items).Id);
            Assert.Equal(new ChatRecoveryIssue(damaged.Id, false), Assert.Single(page.RecoveryIssues));
            Assert.Equal(healthy.Messages, (await reader.LoadAsync(healthy.Id))!.Messages);
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.LoadAsync(damaged.Id));
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.SaveChatAsync(damaged));
            await reader.FlushAsync();
            Assert.Equal(evidence, File.ReadAllBytes(index));
            Assert.True(File.Exists(Path.Combine(_root, "catalog.dirty")));
            reader = Repository();
        }
        await reader.DeleteAsync(damaged.Id);
        await reader.FlushAsync();
        Assert.Empty((await Repository().ListAsync(0, 10)).RecoveryIssues);
        Assert.False(File.Exists(Path.Combine(_root, "catalog.dirty")));
    }

    [Fact]
    public async Task FailedCommitPreservesThePreviousConversationAndCanRetry()
    {
        var chat = Chat(2);
        await Repository().SaveChatAsync(chat);
        bool fail = true;
        var repository = new EncryptedChatRepository(_root, value =>
        {
            if (fail && value.Contains("\"Summary\"", StringComparison.Ordinal)) throw new IOException("Simulated manifest write failure");
            return DataProtection.Protect(value);
        }, DataProtection.Unprotect, () => []);
        chat.Messages.Add(new("user", "uncommitted turn"));
        await Assert.ThrowsAsync<IOException>(() => repository.SaveChatAsync(chat));
        Assert.Equal(2, (await Repository().LoadAsync(chat.Id))!.Messages.Count);
        fail = false;
        await repository.SaveChatAsync(chat);
        Assert.Equal(3, (await Repository().LoadAsync(chat.Id))!.Messages.Count);
    }

    [Fact]
    public async Task CatalogLoadsAfterFlushAndUnflushedChangesForceManifestRecovery()
    {
        var repository = Repository();
        var first = Chat(2);
        await repository.SaveChatAsync(first);
        await repository.FlushAsync();
        Assert.False(File.Exists(Path.Combine(_root, "catalog.dirty")));
        Assert.Single((await Repository().ListAsync(0, 10)).Items);
        var second = Chat(2);
        await repository.SaveChatAsync(second);
        Assert.True(File.Exists(Path.Combine(_root, "catalog.dirty")));
        Assert.Equal(2, (await Repository().ListAsync(0, 10)).TotalCount);
        await repository.FlushAsync();
        await repository.DeleteAsync(first.Id);
        Assert.Equal(second.Id, Assert.Single((await Repository().ListAsync(0, 10)).Items).Id);
    }

    [Fact]
    public async Task SessionRetainsFailedSnapshotsAcrossNavigationAndFlushRetries()
    {
        bool fail = true;
        var repository = new EncryptedChatRepository(_root, value =>
        {
            if (fail && value.Contains("\"Summary\"", StringComparison.Ordinal)) throw new IOException("Simulated full disk");
            return DataProtection.Protect(value);
        }, DataProtection.Unprotect, () => []);
        var session = new ChatSession(repository);
        var first = Chat(2);
        await Assert.ThrowsAsync<AggregateException>(() => session.SaveAsync(first));
        first.Messages.Clear();
        var second = Chat(4);
        fail = false;
        // Navigation can save another conversation while the first snapshot awaits a retry.
        await Assert.ThrowsAsync<AggregateException>(() => session.SaveAsync(second));
        await session.FlushAsync();
        Assert.Equal(2, (await repository.LoadAsync(first.Id))!.Messages.Count);
        Assert.Equal(4, (await repository.LoadAsync(second.Id))!.Messages.Count);
        await session.FlushAsync();
    }

    [Fact]
    public async Task WriteQueueRejectsOverflowButDrainsAcceptedWorkInOrder()
    {
        var queue = new BoundedSerialQueue(2);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var order = new List<int>();
        var first = queue.Enqueue(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            order.Add(1); return true;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var second = queue.Enqueue(() => { order.Add(2); return true; });
        var third = queue.Enqueue(() => { order.Add(3); return true; });
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => queue.Enqueue(() => true)); }
        finally { release.Set(); }
        await queue.CompleteAsync();
        await Task.WhenAll(first, second, third);
        Assert.Equal(new[] { 1, 2, 3 }, order);
        Assert.Throws<ObjectDisposedException>(() => { _ = queue.Enqueue(() => true); });
    }

    [Fact]
    public async Task LatestQueryCancelsActiveAndPendingQueries()
    {
        var queries = new LatestQuery<int>();
        using var entered = new ManualResetEventSlim();
        var first = queries.Submit(token =>
        {
            entered.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }, default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var latest = queries.Submit(_ => 42, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(42, await latest);
        await queries.CompleteAsync();
    }

    [Fact]
    public void ApiContextKeepsWholeTurnsAndOmitsIncompleteResponses()
    {
        var messages = Chat(80).Messages;
        messages.Add(new("user", "failed prompt"));
        messages.Add(new("assistant", "partial") { IsIncomplete = true });
        messages.Add(new("user", "retry"));
        var context = ChatSession.RequestContext(messages);
        Assert.InRange(context.Count, 1, 21);
        Assert.Equal("user", context[0].Role);
        Assert.Equal("retry", context[^1].Content);
        Assert.DoesNotContain(context, m => m.Content is "failed prompt" or "partial");
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
