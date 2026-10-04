using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowFileShelfPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(30)]
    public void ShelfRealizesVisibleItemsAndPreservesSelectionThroughPinReordering(int count) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        string directory = Directory.CreateTempSubdirectory("vnotch-shelf-presentation-").FullName;
        var window = fixture.Window;
        var shelf = Field<FileShelfController>(window, "_fileShelf");
        try
        {
            Set(window, "_isSecondaryView", true);
            window.SecondaryContent.Visibility = Visibility.Visible;
            window.SecondaryContent.Measure(new Size(400, 180));
            window.SecondaryContent.Arrange(new Rect(0, 0, 400, 180));
            string[] paths = Enumerable.Range(0, count).Select(i => Path.Combine(directory, $"item-{i:D2}.txt")).ToArray();
            foreach (string path in paths)
            {
                File.WriteAllText(path, "fixture");
                Assert.True(shelf.AddFileDirect(path));
            }
            window.SecondaryContent.UpdateLayout();
            Invoke(window, "RefreshShelfLayout", true);
            window.SecondaryContent.UpdateLayout();
            Assert.Equal(count, shelf.FileCount);
            Assert.Equal(count > 4, Field<bool>(window, "_shelfUsesSmallItems"));
            Assert.NotEmpty(window.ShelfItemsContainer.Children);
            Assert.True(window.ShelfItemsContainer.Children.Count <= count);
            Assert.Equal(Visibility.Collapsed, window.ShelfPlaceholderPanel.Visibility);
            Assert.Equal(Visibility.Visible, window.ShelfCountBadge.Visibility);
            var item = window.ShelfItemsContainer.Children.OfType<Border>().First();
            string itemPath = Assert.IsType<string>(item.Tag);
            item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            item.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            item.RaiseEvent(MouseArgs(UIElement.MouseLeftButtonDownEvent));
            Assert.True(shelf.IsSelected(itemPath));
            item.RaiseEvent(MouseArgs(UIElement.MouseLeftButtonUpEvent));
            shelf.SelectAll();
            Set(window, "_wasSelectedOnMouseDown", true);
            item.RaiseEvent(MouseArgs(UIElement.MouseLeftButtonUpEvent));
            Assert.Equal(itemPath, Assert.Single(shelf.SelectedFiles));
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
            Assert.True(shelf.IsPinned(itemPath));
            Invoke(window, "RefreshShelfLayout", true);
            Assert.Equal(itemPath, shelf.Files[0]);
            Assert.True(shelf.IsSelected(itemPath));
            Invoke(window, "QueueShelfViewportRefresh");
            Invoke(window, "QueueShelfViewportRefresh");
            await WpfFrameWaiter.UntilAsync(() => !Field<bool>(window, "_shelfViewportRefreshQueued"), "shelf viewport realized", ct);
            shelf.TogglePin(itemPath);
            var first = window.ShelfItemsContainer.Children.OfType<Border>().First();
            Invoke(window, "AnimatePinnedRejection", first);
            await WpfFrameWaiter.UntilAsync(() => first.BorderThickness == new Thickness(1), "pinned file feedback finished", ct);
            int completions = 0;
            Invoke(window, "AnimateFileDeletion", new[] { itemPath }, (Action)(() =>
            {
                shelf.RemoveFile(itemPath);
                Invoke(window, "RefreshShelfLayout", false);
                completions++;
            }));
            await WpfFrameWaiter.UntilAsync(() => completions == 1, "shelf deletion animation", ct);
            Assert.DoesNotContain(itemPath, shelf.Files);
            Assert.True(File.Exists(itemPath));
            Invoke(window, "AnimateFileDeletion", new[] { Path.Combine(directory, "absent.txt") }, (Action)(() => completions++));
            Assert.Equal(2, completions);
        }
        finally
        {
            shelf.Dispose();
            DeleteTemporaryDirectory(directory);
        }
    });

    [Fact]
    public void ShelfDropFeedbackAndUnlockDismissRestoreEmptyState() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Set(window, "_isSecondaryView", true);
        Invoke(window, "SetShelfDropAcceptVisualState");
        var accept = window.FileShelf.Background;
        Invoke(window, "SetShelfDropRejectVisualState", "Rejected fixture");
        Assert.NotSame(accept, window.FileShelf.Background);
        Assert.Equal("Rejected fixture", window.ShelfStatusText.Text);
        Assert.Equal(Visibility.Visible, window.ShelfStatusText.Visibility);
        Invoke(window, "SetShelfDropUnlockHintVisualState");
        Assert.Equal(Loc.Get("shelf.unlockHint"), window.ShelfStatusText.Text);
        Set(window, "_pendingUnlockFiles", new[] { "unused fixture" });
        Invoke(window, "ShowShelfUnlockBanner", 31);
        Assert.Equal(Visibility.Visible, window.ShelfUnlockBanner.Visibility);
        Assert.Equal(Visibility.Collapsed, window.ShelfCountBadge.Visibility);
        Invoke(window, "ShelfUnlockDismiss_Click", window, new RoutedEventArgs());
        await WpfFrameWaiter.UntilAsync(() => window.ShelfUnlockBanner.Visibility == Visibility.Collapsed, "shelf unlock dismissed", ct);
        Assert.Null(Field<string[]?>(window, "_pendingUnlockFiles"));
        Assert.Equal(Visibility.Visible, window.ShelfPlaceholderPanel.Visibility);
        Invoke(window, "ResetShelfDropVisualState");
        Assert.Equal(Visibility.Collapsed, window.ShelfStatusText.Visibility);
        Set(window, "_isSelecting", true);
        Set(window, "_selectionStart", new Point(0, 0));
        window.FileShelfGrid.Measure(new Size(300, 150));
        window.FileShelfGrid.Arrange(new Rect(0, 0, 300, 150));
        Invoke(window, "HandleRectangleSelection", new Point(500, 500));
        Assert.InRange(window.SelectionRect.Width, 0, 300);
        Assert.InRange(window.SelectionRect.Height, 0, 150);
        Invoke(window, "FileShelfGrid_LostMouseCapture", window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        Assert.False(Field<bool>(window, "_isSelecting"));
        Assert.Equal(Visibility.Collapsed, window.SelectionCanvas.Visibility);
        Invoke(window, "CancelLassoSelection");
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        configureSettings: settings => { settings.EnableLocalOnlyMode = true; settings.ShelfWidget = "none"; },
        configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static MouseButtonEventArgs MouseArgs(RoutedEvent routedEvent) => new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routedEvent };
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value)
    {
        var property = typeof(MainWindow).GetProperty(name, Private);
        if (property != null) property.SetValue(window, value);
        else typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    }
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static void DeleteTemporaryDirectory(string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not a temporary directory.");
        Directory.Delete(target, recursive: true);
    }
}

