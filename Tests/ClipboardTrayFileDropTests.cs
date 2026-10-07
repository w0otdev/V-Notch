using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardTrayFileDropTests
{
    [Theory]
    [InlineData("All")]
    [InlineData("Pin")]
    [InlineData("Archive")]
    [InlineData("Work")]
    public void FiveDroppedFilesBecomeFiveIndependentCards(string category) => SharedStaTestRunner.RunAsync(async ct =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-five-files-").FullName;
        try
        {
            string[] names = ["V-Notch.exe.sha256", "V-Notch-Setup.exe", "V-Notch-Setup.exe.manifest.json", "V-Notch-Setup.exe.manifest.sig", "V-Notch-Setup.exe.sha256"];
            string[] paths = names.Select(name => Path.Combine(root, name)).ToArray();
            foreach (string path in paths) File.WriteAllText(path, Path.GetFileName(path));
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, new ClipboardHistoryStore(Path.Combine(root, "history")));
            int drops = 0;
            controller.DropImported += () => drops++;
            var data = new DataObject(DataFormats.FileDrop, paths);
            await controller.ImportDropAsync(data, category);
            await WpfFrameWaiter.UntilAsync(() => drops == 1, "one completed drop", ct);
            var entries = await controller.Store.SearchAsync("", category);
            Assert.Equal(5, entries.Count);
            Assert.Equal(names.Order(), entries.Select(entry => entry.Title).Order());
            foreach (var entry in entries)
            {
                Assert.Equal(1, entry.FileCount);
                Assert.Equal(Path.GetExtension(entry.Title), entry.PrimaryExtension);
                var content = await controller.Store.GetContentAsync(entry.Id);
                Assert.Equal(entry.Title, Assert.Single(content.Files).Name);
                var export = Assert.Single(await controller.Store.ExportFilesAsync(entry.Id));
                Assert.Equal(entry.Title, await File.ReadAllTextAsync(export, ct));
            }
            await controller.ImportDropAsync(data, category);
            Assert.Equal(5, (await controller.Store.SearchAsync("", category)).Count);
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void MixedImageAndFileDropKeepsEachPreviewWithItsOwnCard() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-mixed-drop-").FullName;
        try
        {
            string imagePath = Path.Combine(root, "photo.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4)));
            using (var output = File.Create(imagePath)) encoder.Save(output);
            string filePath = Path.Combine(root, "manifest.json");
            File.WriteAllText(filePath, "file fixture");
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, new ClipboardHistoryStore(Path.Combine(root, "history")));
            using var tray = new ClipboardTray();
            tray.Attach(controller);
            await tray.ImportCurrentCategoryDropAsync(new DataObject(DataFormats.FileDrop, new[] { imagePath, filePath }));
            var entries = controller.Store.Snapshot();
            Assert.Equal(2, entries.Count);
            var image = Assert.Single(entries, entry => entry.Kind == ClipboardKind.Image);
            Assert.Equal("photo.png", image.Title);
            Assert.NotNull(await controller.Store.GetImageAsync(image.Id));
            var file = Assert.Single(entries, entry => entry.Kind == ClipboardKind.File);
            Assert.Equal("manifest.json", file.Title);
            Assert.Null(await controller.Store.GetImageAsync(file.Id));
            Assert.False(ClipboardTray.CanDropCategory("Files", [], [imagePath, filePath]));
            Assert.False(ClipboardTray.CanDropCategory("Images", [], [imagePath, filePath]));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void LargeDropKeepsAggregateApprovalAndSplitsAfterApproval() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-drop-approval-").FullName;
        try
        {
            string[] paths = [Path.Combine(root, "one.bin"), Path.Combine(root, "two.bin")];
            foreach (string path in paths)
            {
                using var file = File.Create(path);
                file.SetLength(26L * 1024 * 1024);
            }
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, new ClipboardHistoryStore(Path.Combine(root, "history")));
            ClipboardCapture? pending = null;
            long size = 0;
            controller.ApprovalRequired += (capture, bytes) => { pending = capture; size = bytes; };
            await controller.ImportDropAsync(new DataObject(DataFormats.FileDrop, paths), "Pin");
            await WpfFrameWaiter.UntilAsync(() => pending != null, "aggregate approval", ct);
            Assert.True(pending!.SeparateFiles);
            Assert.Equal(52L * 1024 * 1024, size);
            Assert.Empty(controller.Store.Snapshot());
            await controller.ImportAsync(pending, approved: true);
            Assert.Equal(2, (await controller.Store.SearchAsync("", "Pin")).Count);
            Assert.All(controller.Store.Snapshot(), entry => Assert.Equal(1, entry.FileCount));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void OneLockedFileDoesNotDiscardOtherDroppedCards() => SharedStaTestRunner.RunAsync(async ct =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-drop-locked-").FullName;
        try
        {
            string[] paths = Enumerable.Range(0, 5).Select(i => Path.Combine(root, $"file-{i}.txt")).ToArray();
            foreach (string path in paths) File.WriteAllText(path, Path.GetFileName(path));
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, new ClipboardHistoryStore(Path.Combine(root, "history")));
            string? status = null;
            controller.StatusChanged += message => status = message;
            using var locked = new FileStream(paths[2], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await controller.ImportDropAsync(new DataObject(DataFormats.FileDrop, paths));
            await WpfFrameWaiter.UntilAsync(() => status != null, "partial drop status", ct);
            Assert.Equal(Loc.Get("clipboard.partialImport", 4, 5, 1), status);
            Assert.Equal(4, controller.Store.Snapshot().Count);
            Assert.DoesNotContain(controller.Store.Snapshot(), entry => entry.Title == "file-2.txt");
        }
        finally { Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData(1, 246)]
    [InlineData(5, 246)]
    [InlineData(1, 300)]
    public void FilePreviewFitsShortCardsBeforeAndAfterIconLoads(int fileCount, double height) => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var card = new ClipboardCardViewModel(new ClipboardEntry
        {
            Kind = ClipboardKind.File, FileCount = fileCount, PrimaryExtension = ".sha256",
            Title = "V-Notch-Setup.exe.manifest.json, V-Notch-Setup.exe.manifest.sig"
        });
        var cards = (ObservableCollection<ClipboardCardViewModel>)typeof(ClipboardTray).GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
        cards.Add(card);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = height };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var badge = Descendants(tray).OfType<Border>().Single(border => border.Name == "FileBadgePanel");
            var icon = Descendants(badge).OfType<Grid>().Single(grid => grid.Name == "FileHeroIcon");
            var name = Descendants(badge).OfType<TextBlock>().Single(text => text.Name == "FileHeroName");
            AssertFits(icon, badge);
            AssertFits(name, badge);
            Assert.True(name.ActualHeight >= 14);
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            bitmap.Freeze();
            card.FileIcon = bitmap;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            AssertFits(icon, badge);
            AssertFits(name, badge);
        }
        finally { window.Close(); }
    });

    private static void AssertFits(FrameworkElement element, FrameworkElement parent)
    {
        var origin = element.TranslatePoint(new Point(), parent);
        Assert.True(origin.X >= 0 && origin.Y >= 0);
        Assert.True(origin.X + element.ActualWidth <= parent.ActualWidth + 1);
        Assert.True(origin.Y + element.ActualHeight <= parent.ActualHeight + 1);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
