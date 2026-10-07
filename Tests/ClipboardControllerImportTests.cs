using System.IO;
using System.Windows;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardControllerImportTests
{
    [Theory]
    [InlineData("Pin")]
    [InlineData("Archive")]
    [InlineData("Work")]
    [InlineData("All")]
    public void DroppedTextIsStoredAndAssignedToTheRequestedCategory(string category) => SharedStaTestRunner.RunAsync(async ct =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-controller-import-").FullName;
        try
        {
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, new ClipboardHistoryStore(root));
            int drops = 0;
            double progress = 0;
            controller.DropImported += () => drops++;
            controller.ImportProgress += value => progress = value;
            var data = new DataObject();
            data.SetText("controller import fixture");
            await controller.ImportDropAsync(data, category);
            await WpfFrameWaiter.UntilAsync(() => drops == 1 && progress == 1, "drop notification", ct);
            var entry = Assert.Single(await controller.Store.SearchAsync("", category));
            Assert.Equal("controller import fixture", (await controller.Store.GetContentAsync(entry.Id)).Text);
            await controller.AssignCategoryAsync(entry, "All");
            Assert.Single(await controller.Store.SearchAsync("", "All"));
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData("data:image/png;base64,not-base64")]
    [InlineData("data:text/plain;base64,aGk=")]
    public void InvalidImageDropReportsFailureWithoutAddingHistory(string url) => SharedStaTestRunner.RunAsync(async ct =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-controller-error-").FullName;
        try
        {
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, new ClipboardHistoryStore(root));
            string? status = null;
            controller.StatusChanged += value => status = value;
            await controller.ImportDropPayloadAsync(new(new ClipboardCapture(), new Uri(url)));
            await WpfFrameWaiter.UntilAsync(() => status != null, "invalid drop status", ct);
            Assert.Equal(Loc.Get("clipboard.readError"), status);
            Assert.Empty(await controller.Store.SearchAsync("", "All"));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void PersonalDropWhileLockedReportsFailureAndNeverStoresPlaintext() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-controller-locked-").FullName;
        try
        {
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, new ClipboardHistoryStore(root));
            string? status = null;
            int drops = 0;
            controller.StatusChanged += value => status = value;
            controller.DropImported += () => drops++;
            await controller.ImportAsync(new ClipboardCapture { Text = "private fixture", TargetCategory = "Personal" }, notifyDrop: true);
            await WpfFrameWaiter.UntilAsync(() => status != null, "locked vault status", ct);
            Assert.Equal(Loc.Get("clipboard.saveError"), status);
            Assert.Equal(0, drops);
            Assert.Empty(await controller.Store.SearchAsync("", "All"));
        }
        finally { Directory.Delete(root, true); }
    });
}
