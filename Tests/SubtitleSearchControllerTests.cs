using VNotch.Controllers;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SubtitleSearchControllerTests
{
    [Fact]
    public async Task VideoIdArrivesAfterUnresolvedAttempt_SearchWidgetAppearsOnlyOnce()
    {
        var controller = new SubtitleSearchController();
        var metadata = Pending<string>();
        var download = Pending<List<LyricLine>?>();
        var events = new List<string>();
        int fetchCount = 0;

        Task<List<LyricLine>?> Fetch(string id)
        {
            Assert.Equal("video", id);
            fetchCount++;
            return download.Task;
        }

        void Started(string id) => events.Add("searching");
        void Completed(List<LyricLine>? lines) => events.Add(lines == null ? "calendar" : "subtitles");

        var unresolved = controller.SearchAsync(() => metadata.Task, Fetch, Started, Completed);
        Assert.Empty(events);
        Assert.Equal(0, fetchCount);

        metadata.SetResult("");
        await unresolved;
        Assert.Equal(new[] { "calendar" }, events);
        Assert.Equal(0, fetchCount);

        // The browser extension delivers the ID after the initial grace period.
        var resolved = controller.SearchAsync(() => Task.FromResult("video"), Fetch, Started, Completed);
        Assert.Equal(new[] { "calendar", "searching" }, events);
        Assert.Equal(1, fetchCount);

        download.SetResult(new() { new(TimeSpan.Zero, "First subtitle") });
        await resolved;
        Assert.Equal(new[] { "calendar", "searching", "subtitles" }, events);
    }

    [Fact]
    public async Task ResolvedVideoWithoutSubtitles_SearchRemainsUntilDownloadFinishes()
    {
        var controller = new SubtitleSearchController();
        var download = Pending<List<LyricLine>?>();
        var events = new List<string>();

        var search = controller.SearchAsync(
            () => Task.FromResult("video"), _ => download.Task,
            _ => events.Add("searching"), lines =>
            {
                Assert.Null(lines);
                events.Add("calendar");
            });

        Assert.Equal(new[] { "searching" }, events);
        download.SetResult(null);
        await search;
        Assert.Equal(new[] { "searching", "calendar" }, events);
    }

    [Fact]
    public async Task NewVideoSupersedesPendingResolution_OldVideoCannotOpenSearchWidget()
    {
        var controller = new SubtitleSearchController();
        var oldMetadata = Pending<string>();
        var events = new List<string>();

        var oldSearch = controller.SearchAsync(
            () => oldMetadata.Task,
            _ => throw new InvalidOperationException("Stale video must not be downloaded"),
            _ => events.Add("old-searching"), _ => events.Add("old-completed"));

        await controller.SearchAsync(
            () => Task.FromResult("new-video"), _ => Task.FromResult<List<LyricLine>?>(null),
            id => events.Add(id), _ => events.Add("new-completed"));
        oldMetadata.SetResult("old-video");
        await oldSearch;

        Assert.Equal(new[] { "new-video", "new-completed" }, events);
    }

    [Fact]
    public async Task OldDownloadFinishesWhileNewVideoIsSearching_DoesNotHideNewSearchWidget()
    {
        var controller = new SubtitleSearchController();
        var oldDownload = Pending<List<LyricLine>?>();
        var newDownload = Pending<List<LyricLine>?>();
        var events = new List<string>();

        var oldSearch = controller.SearchAsync(
            () => Task.FromResult("old"), _ => oldDownload.Task,
            id => events.Add(id), _ => events.Add("old-completed"));
        var newSearch = controller.SearchAsync(
            () => Task.FromResult("new"), _ => newDownload.Task,
            id => events.Add(id), _ => events.Add("new-completed"));

        oldDownload.SetResult(null);
        await oldSearch;
        Assert.Equal(new[] { "old", "new" }, events);

        newDownload.SetResult(new() { new(TimeSpan.Zero, "New video subtitle") });
        await newSearch;
        Assert.Equal(new[] { "old", "new", "new-completed" }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidateDuringSearch_PendingResultCannotReopenWidget(bool downloadStarted)
    {
        var controller = new SubtitleSearchController();
        var metadata = Pending<string>();
        var download = Pending<List<LyricLine>?>();
        var events = new List<string>();

        var search = controller.SearchAsync(
            () => downloadStarted ? Task.FromResult("video") : metadata.Task,
            _ => download.Task, _ => events.Add("searching"), _ => events.Add("completed"));
        controller.Invalidate();
        events.Clear();

        metadata.SetResult("video");
        download.SetResult(new() { new(TimeSpan.Zero, "Stale subtitle") });
        await search;
        Assert.Empty(events);
    }

    private static TaskCompletionSource<T> Pending<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
