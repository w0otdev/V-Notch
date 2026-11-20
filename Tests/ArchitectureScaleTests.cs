using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VNotch.ApplicationServices;
using VNotch.Infrastructure.Windows;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ArchitectureScaleTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "ArchitectureScale")]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task EncryptedStorageQueriesAndFileRecallAtOneAndTenTimes(int count)
    {
        string root = SearchFixtureDirectory.Create();
        try
        {
            var repository = new EncryptedChatRepository(Path.Combine(root, "chats"), DataProtection.Protect, DataProtection.Unprotect, () => []);
            string text = new('x', 1000);
            SpotlightSavedChat? last = null;
            for (int i = 0; i < count; i++)
            {
                last = new() { Provider = "benchmark", Model = "synthetic", Messages = Enumerable.Range(0, 20)
                    .Select(j => new SpotlightAiMessage(j % 2 == 0 ? "user" : "assistant", $"{i}:{j} {text}")).ToList() };
                await repository.SaveChatAsync(last);
            }
            var recovery = new EncryptedChatRepository(Path.Combine(root, "chats"), DataProtection.Protect, DataProtection.Unprotect, () => []);
            var metadataRebuild = await Measure(async () => Assert.Equal(count, (await recovery.ListAsync(0, 40)).TotalCount), 1);
            await recovery.FlushAsync();
            var cold = new EncryptedChatRepository(Path.Combine(root, "chats"), DataProtection.Protect, DataProtection.Unprotect, () => []);
            var coldRead = await Measure(async () => Assert.Equal(count, (await cold.ListAsync(0, 40)).TotalCount), 1);
            var pageRead = await Measure(async () => Assert.Equal(40, (await cold.ListAsync(0, 40)).Items.Count));
            var tailRead = await Measure(async () => Assert.Equal(20, (await cold.LoadAsync(last!.Id))!.Messages.Count));
            var save = await Measure(async () => { last!.Draft = Guid.NewGuid().ToString("N"); await cold.SaveChatAsync(last); });
            Assert.True(coldRead.AllocatedP95 < count * 32000L, "Cold startup must load metadata, not all transcript bodies.");
            Assert.True(pageRead.AllocatedP95 < 1_000_000, "A warm page must not allocate the entire history.");
            Assert.True(save.AllocatedP95 < 4_000_000, "A one-chat save must not serialize unrelated chats.");

            await using var clipboard = new ClipboardHistoryStore(Path.Combine(root, "clipboard")) { MaxUnpinnedEntries = count + 1 };
            await clipboard.InitializeAsync();
            for (int i = 0; i < count; i++)
                await clipboard.ImportAsync(new ClipboardCapture { Text = $"Synthetic entry {i} " + new string('z', 1200) + " searchable-tail" });
            var clipboardPage = await Measure(async () => Assert.Equal(40, (await clipboard.SearchPageAsync("", "All", 0, 40)).Entries.Count));
            var clipboardText = await Measure(async () => Assert.Equal(40, (await clipboard.SearchPageAsync("searchable-tail", "All", 0, 40)).Entries.Count));
            Assert.True(clipboardPage.AllocatedP95 < 2_000_000, "Metadata query allocation must remain bounded by a page.");

            string files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            int targets = 0;
            for (int i = 0; i < count; i++)
            {
                bool target = i % 20 == 0;
                if (target) targets++;
                File.WriteAllText(Path.Combine(files, $"{(target ? "target" : "other")}-{i:D4}.txt"), "synthetic");
            }
            var provider = new LocalFileSearchProvider([files], 5000);
            var watch = Stopwatch.StartNew();
            var found = await provider.SearchBatchAsync("target", 100, default);
            double fileMilliseconds = watch.Elapsed.TotalMilliseconds;
            Assert.Equal(SearchCompleteness.Complete, found.Completeness);
            Assert.Equal(targets, found.Results.Count);
            var budgeted = await new LocalFileSearchProvider([files], 0).SearchBatchAsync("target", 100, default);
            Assert.Equal(SearchCompleteness.Partial, budgeted.Completeness);

            var queue = new BoundedSerialQueue(32);
            var latencies = new List<double>();
            var queued = Enumerable.Range(0, 32).Select(_ =>
            {
                long submitted = Stopwatch.GetTimestamp();
                return queue.Enqueue(() => { latencies.Add(Stopwatch.GetElapsedTime(submitted).TotalMilliseconds); return true; });
            }).ToArray();
            await Task.WhenAll(queued);
            await queue.CompleteAsync();
            string json = JsonSerializer.Serialize(new
            {
                count, messagesPerChat = 20, charactersPerMessage = 1000, samples = 20,
                runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount,
                coldRead, metadataRebuild, pageRead, tailRead, save, clipboardPage, clipboardText,
                fileMilliseconds, fileRecall = (double)found.Results.Count / targets,
                budgetedCompleteness = budgeted.Completeness.ToString(),
                queueWaitP95Ms = Percentile(latencies),
                limitations = "Synthetic local disk; warm OS cache; managed allocations only; no live provider load or GPU measurement. Cold startup is one sample."
            }, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(json);
            string results = Environment.GetEnvironmentVariable("VNOTCH_SCALE_RESULTS") ?? DefaultResultsDirectory();
            Directory.CreateDirectory(results);
            await File.WriteAllTextAsync(Path.Combine(results, $"scale-{count}.json"), json);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed record Measurement(double P95Milliseconds, long AllocatedP95);
    private static async Task<Measurement> Measure(Func<Task> action, int samples = 20)
    {
        var milliseconds = new List<double>();
        var allocations = new List<double>();
        for (int i = 0; i < samples; i++)
        {
            long allocated = GC.GetTotalAllocatedBytes(true);
            long started = Stopwatch.GetTimestamp();
            await action();
            milliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
        }
        return new(Percentile(milliseconds), (long)Percentile(allocations));
    }
    private static double Percentile(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
    private static string DefaultResultsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "V-Notch.sln"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, "artifacts", "architecture-scale");
    }
}
