using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
public sealed class ClipboardTrayRegressionTests
{
    [Fact]
    public void ReconcileMetadataPreservesCardSelectionAndContainer() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var original = new ClipboardCardViewModel(new ClipboardEntry { Title = "before" });
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        cards.Add(original);
        var list = (ListBox)tray.FindName("Cards");
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            list.SelectedItem = original;
            var container = list.ItemContainerGenerator.ContainerFromItem(original);
            int replacements = 0;
            cards.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace) replacements++; };
            Invoke(tray, "ReconcileCards", (object)new[] { original.Entry with { Title = "after", IsPinned = true } });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(original, Assert.Single(cards));
            Assert.Same(original, list.SelectedItem);
            Assert.Same(container, list.ItemContainerGenerator.ContainerFromItem(original));
            Assert.Equal(0, replacements);
            Assert.Equal("after", original.Title);
            Assert.True(original.Entry.IsPinned);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ActionBindingsResetWhenTheSameVisualGetsAnotherCard() => SharedStaTestRunner.RunAsync(async () =>
    {
        Loc.SetLanguage("en");
        using var tray = new ClipboardTray();
        var list = (ListBox)tray.FindName("Cards");
        var visual = (FrameworkElement)list.ItemTemplate.LoadContent();
        visual.DataContext = new ClipboardCardViewModel(new ClipboardEntry { IsPinned = true, IsArchived = true, IsPersonal = true });
        int loads = 0;
        visual.Loaded += (_, _) => loads++;
        var window = new BackgroundWindow { Content = visual, Width = 250, Height = 230 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var buttons = Descendants(visual).OfType<Button>().ToArray();
            var actions = buttons.Where(button => button.Tag is string).ToDictionary(button => (string)button.Tag);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)actions["pin"].Background).Color);
            Assert.Equal(Color.FromRgb(255, 214, 10), ((SolidColorBrush)actions["pin"].Foreground).Color);
            Assert.Equal(Loc.Get("clipboard.unpin"), actions["pin"].ToolTip);
            Assert.Equal(((ClipboardCardViewModel)visual.DataContext).MoreLabel, actions["more"].ToolTip);

            visual.DataContext = new ClipboardCardViewModel(new ClipboardEntry());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, loads);
            Assert.Equal(Colors.Transparent, ((SolidColorBrush)actions["pin"].Background).Color);
            Assert.Equal(Colors.White, ((SolidColorBrush)actions["pin"].Foreground).Color);
            Assert.Equal(0.7, actions["pin"].Opacity, 2);
            Assert.Equal(Loc.Get("clipboard.pin"), actions["pin"].ToolTip);
            Assert.Equal(((ClipboardCardViewModel)visual.DataContext).MoreLabel, actions["more"].ToolTip);
            Assert.All(buttons, button => Assert.Equal(button.ToolTip, AutomationProperties.GetName(button)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void CategoryUpdatesKeepTheirVisualsAndUnloadingStopsRendering() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var categories = (ItemsControl)tray.FindName("CategoryPanel");
            var buttons = Descendants(categories).OfType<Button>().ToArray();
            Assert.Equal(16, buttons.Length);
            SetField(tray, "_categoryCounts", new Dictionary<string, int> { ["Images"] = 42 });
            SetField(tray, "_category", "Images");
            Invoke(tray, "RebuildCategories");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(buttons, Descendants(categories).OfType<Button>());
            var imageCategory = Assert.Single(categories.Items.Cast<ClipboardCategoryViewModel>(), category => category.Name == "Images");
            Assert.Equal(42, imageCategory.Count);
            var imageButton = buttons.Single(button => Equals(button.Tag, "Images"));
            Assert.Equal(((SolidColorBrush)tray.FindResource("TrayAccent")).Color, ((SolidColorBrush)imageButton.Background).Color);
            Invoke(tray, "Category_Wheel", tray, new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            Assert.True(Field<bool>(tray, "_categoryScrollActive"));
            window.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(Field<bool>(tray, "_categoryScrollActive"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void RecycledAndUnloadedCardsCancelQueuedThumbnailWork() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-thumbnail-regression-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var slots = Field<SemaphoreSlim>(tray, "_thumbnailSlots");
        await slots.WaitAsync(); await slots.WaitAsync();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = TestImage() })).Entry!;
            tray.Attach(controller);
            Field<DispatcherTimer>(tray, "_searchTimer").Stop();
            SetField(tray, "_queryDirty", false);
            Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(new ClipboardCardViewModel(entry));
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var loads = Field<IDictionary>(tray, "_thumbnailLoads");
            var element = Assert.Single(loads.Keys.Cast<FrameworkElement>());
            var firstLoad = loads[element]!;
            var firstToken = LoadToken(firstLoad);
            element.DataContext = new ClipboardCardViewModel(entry);
            Assert.True(firstToken.IsCancellationRequested);
            // Replacement loads are coalesced after the current layout/input pass.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var replacementLoad = loads[element]!;
            var replacementToken = LoadToken(replacementLoad);
            await LoadCompletion(firstLoad).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(replacementLoad, loads[element]); // Old cleanup must not remove the recycled card's load.
            Assert.False(replacementToken.IsCancellationRequested);
            window.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await LoadCompletion(replacementLoad).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(replacementToken.IsCancellationRequested);
            Assert.Empty(loads.Keys.Cast<object>());
            Assert.Equal(0, slots.CurrentCount); // Canceled waiters never acquired a decode slot.
            Assert.Empty(Field<HashSet<FrameworkElement>>(tray, "_realizedCardElements"));
        }
        finally
        {
            window.Close(); tray.Dispose();
            slots.Release(2);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            controller.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void UnloadingAfterAcquiringASlotReleasesItExactlyOnce() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-acquired-thumbnail-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        var slots = Field<SemaphoreSlim>(tray, "_thumbnailSlots");
        await slots.WaitAsync(); // Leave one slot for the real thumbnail request.
        Task? blocker = null;
        Task? completion = null;
        void BlockQueue() { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = TestImage() })).Entry!;
            store.Changed += BlockQueue;
            blocker = store.ImportAsync(new ClipboardCapture { Text = "block image read" });
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            tray.Attach(controller);
            Field<DispatcherTimer>(tray, "_searchTimer").Stop(); SetField(tray, "_queryDirty", false);
            Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(new ClipboardCardViewModel(entry));
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var loads = Field<IDictionary>(tray, "_thumbnailLoads");
            var load = Assert.Single(loads.Values.Cast<object>());
            var token = LoadToken(load);
            completion = LoadCompletion(load);
            Assert.Equal(0, slots.CurrentCount);
            window.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(token.IsCancellationRequested);
            Assert.False(completion.IsCompleted);
            Assert.Equal(0, slots.CurrentCount);
            release.Set();
            await blocker;
            store.Changed -= BlockQueue;
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, slots.CurrentCount);
            Assert.Empty(loads.Keys.Cast<object>());
        }
        finally
        {
            release.Set(); window.Close(); tray.Dispose();
            if (blocker != null) await blocker;
            store.Changed -= BlockQueue;
            if (completion != null) await completion.WaitAsync(TimeSpan.FromSeconds(5));
            slots.Release();
            controller.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void CollectionMotionWaitsForTheNormalLayoutPass() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        var first = new ClipboardCardViewModel(new ClipboardEntry { Id = Guid.NewGuid() });
        var second = new ClipboardCardViewModel(new ClipboardEntry { Id = Guid.NewGuid() });
        cards.Add(first); cards.Add(second);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SetField(tray, "_animateResults", false);
            var positions = (Dictionary<Guid, double>)Invoke(tray, "CaptureCardPositions")!;
            Assert.Equal(2, positions.Count);
            Invoke(tray, "ReconcileCards", (object)new[] { second.Entry, first.Entry });
            var list = (ListBox)tray.FindName("Cards");
            var panel = Descendants(list).OfType<VirtualizingStackPanel>().Single();
            Assert.False(panel.IsMeasureValid);
            Invoke(tray, "AnimateCardPositions", positions);
            Assert.False(panel.IsMeasureValid);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(panel.IsMeasureValid);
            Assert.Null(Field<object?>(tray, "_pendingCardPositions"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void UiTaskBoundaryHandlesUnexpectedExceptions() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        await (Task)Invoke(tray, "SafeAsync", (Func<Task>)(() => throw new ArgumentException("test")))!;
        Assert.Equal(Loc.Get("clipboard.error"), ((TextBlock)tray.FindName("StatusText")).Text);
    });

    [Fact]
    public void IconReusesFrozenDrawingResourcesAndRejectsInvalidKinds() => SharedStaTestRunner.RunAsync(async () =>
    {
        var icon = new TrayIcon { Kind = TrayIconKind.Text, Foreground = Brushes.White };
        var window = new BackgroundWindow { Content = icon, Width = 32, Height = 40 };
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        try
        {
            GeometryDrawing Draw()
            {
                var drawing = new DrawingGroup();
                using (var context = drawing.Open())
                    typeof(TrayIcon).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(icon, [context]);
                return GeometryDrawings(drawing).Single();
            }
            var first = Draw(); var second = Draw();
            Assert.Same(first.Pen, second.Pen);
            Assert.True(first.Pen.IsFrozen);
            Assert.Same(first.Geometry, second.Geometry);
            foreach (var kind in Enum.GetValues<TrayIconKind>()) { icon.Kind = kind; Assert.NotNull(Draw().Geometry); }
            Assert.Throws<ArgumentException>(() => icon.Kind = (TrayIconKind)999);
            var mutable = new SolidColorBrush(Colors.Red);
            icon.Kind = TrayIconKind.Text; icon.Foreground = mutable;
            var mutablePen = Draw().Pen;
            mutable.Color = Colors.Blue;
            Assert.Same(mutablePen, Draw().Pen);
            Assert.Equal(Colors.Blue, ((SolidColorBrush)Draw().Pen.Brush).Color);
            Assert.False(mutable.IsFrozen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EveryTrayIconPaintsBeforeDeferredSizeNotification() => SharedStaTestRunner.Run(() =>
    {
        foreach (var kind in Enum.GetValues<TrayIconKind>())
            foreach (double size in new[] { 14d, 16d, 21d })
            {
                var icon = new TrayIcon { Kind = kind, Width = size, Height = size, Foreground = Brushes.White };
                icon.Measure(new Size(size, size));
                icon.Arrange(new Rect(0, 0, size, size));
                typeof(TrayIcon).GetField("_viewport", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(icon, null);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    typeof(TrayIcon).GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(icon, [dc]);
                Assert.False(visual.ContentBounds.IsEmpty);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size, (int)size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[(int)size * (int)size * 4];
                bitmap.CopyPixels(pixels, (int)size * 4, 0);
                Assert.True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 128) >= 8, $"{kind} at {size} must paint visible pixels");
            }
    });

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345x")]
    [InlineData("１２３４５６")]
    public void InvalidPasscodeDoesNotLeavePartialDigits(string digits)
    {
        using var pin = Pin(digits);
        byte[] output = [255, 255, 255, 255, 255, 255];
        Assert.False(ClipboardPasscode.TryCopyDigits(pin, output));
        Assert.All(output, digit => Assert.Equal(0, digit));
    }

    [Fact]
    public void SecurePinUnlocksVaultWrittenWithTheLegacyStringFormat()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-vault-v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        string pepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        byte[] password = Encoding.UTF8.GetBytes("123456" + pepper);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
        try
        {
            byte[] verification = new byte[28 + "VNotch.Personal.v1"u8.Length];
            RandomNumberGenerator.Fill(verification.AsSpan(0, 12));
            using (var cipher = new AesGcm(key, 16))
                cipher.Encrypt(verification.AsSpan(0, 12), "VNotch.Personal.v1"u8, verification.AsSpan(28), verification.AsSpan(12, 16));
            File.WriteAllText(Path.Combine(root, "personal-key.json"), JsonSerializer.Serialize(new
            { Version = 1, Salt = salt, Pepper = DataProtection.Protect(pepper), Verification = verification }));
            using var vault = new ClipboardVault(root);
            using var pin = Pin("123456");
            Assert.True(vault.Unlock(pin, false));
            Assert.Equal("secret"u8.ToArray(), vault.Open(vault.Seal("secret"u8)));
        }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(key); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnlockOwnsTheSecureInputWhileWaitingForTheStoreQueue()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-pin-ownership-" + Guid.NewGuid().ToString("N"));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            void BlockQueue() { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            store.Changed += BlockQueue;
            Task import = store.ImportAsync(new ClipboardCapture { Text = "queue blocker" });
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            Task<bool> unlock;
            using (var pin = Pin("123456")) unlock = store.UnlockPersonalAsync(pin, create: true);
            release.Set();
            await import;
            store.Changed -= BlockQueue;
            Assert.True(await unlock);
        }
        finally { release.Set(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EntryLookupReturnsCurrentMetadataAndHidesLockedPersonalEntries()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-entry-lookup-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { Text = "entry lookup" })).Entry!;
            Assert.Same(entry, store.GetEntry(entry.Id));
            await store.UpdateAsync(entry.Id, groups: ["Work"], pinned: true);
            var updated = store.GetEntry(entry.Id)!;
            Assert.True(updated.IsPinned);
            Assert.Equal(new[] { "Work" }, updated.Groups);
            using var pin = Pin("123456");
            Assert.True(await store.UnlockPersonalAsync(pin, create: true));
            await store.MovePersonalAsync(entry.Id, true);
            Assert.True(store.GetEntry(entry.Id)!.IsPersonal);
            await store.LockPersonalAsync();
            Assert.Null(store.GetEntry(entry.Id));
            Assert.True(await store.UnlockPersonalAsync(pin));
            await store.DeleteAsync(entry.Id);
            Assert.Null(store.GetEntry(entry.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CanceledImageReadDoesNotBreakTheStoreQueue()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-image-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            byte[] png = TestImage();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = png })).Entry!;
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetImageAsync(entry.Id, cancellation.Token));
            Assert.Equal(png, await store.GetImageAsync(entry.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PinMismatchKeepsConfirmationAndBackRestartsSetup() => SharedStaTestRunner.RunAsync(async () =>
    {
        Loc.SetLanguage("en");
        string root = Path.Combine(Path.GetTempPath(), "vnotch-pin-ux-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
            using var tray = new ClipboardTray();
            tray.Attach(controller);
            var box = (PasswordBox)tray.FindName("PasscodeBox");
            box.Password = "123456";
            await (Task)Invoke(tray, "UnlockPersonalAsync")!;
            var firstPin = Field<SecureString>(tray, "_newPin");
            box.Password = "654321";
            await (Task)Invoke(tray, "UnlockPersonalAsync")!;
            Assert.Same(firstPin, Field<SecureString>(tray, "_newPin"));
            Assert.Equal(Loc.Get("clipboard.confirmRetry"), ((TextBlock)tray.FindName("LockHint")).Text);
            Invoke(tray, "PinBack_Click", tray, new RoutedEventArgs());
            Assert.Null(Field<SecureString?>(tray, "_newPin"));
            Assert.Equal(Loc.Get("clipboard.createStep"), ((TextBlock)tray.FindName("LockHint")).Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public void ResetRequiresExplicitTokenAndCloseClearsConsent() => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        Invoke(tray, "ForgotPin_Click", tray, new RoutedEventArgs());
        var box = (TextBox)tray.FindName("ResetConfirmationBox");
        var button = (Button)tray.FindName("ResetPersonalButton");
        Assert.False(button.IsEnabled);
        box.Text = "delete";
        Assert.False(button.IsEnabled);
        box.Text = "DELETE";
        Assert.True(button.IsEnabled);
        Invoke(tray, "CloseDetails");
        Assert.Empty(box.Text);
        Assert.False(button.IsEnabled);
    });

    [Theory]
    [InlineData("de")]
    [InlineData("ru")]
    public void ResetActionsFitNarrowLocalizedPanel(string language) => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage(language);
        using var tray = new ClipboardTray();
        Invoke(tray, "ForgotPin_Click", tray, new RoutedEventArgs());
        tray.Measure(new Size(400, 340));
        tray.Arrange(new Rect(0, 0, 400, 340));
        tray.UpdateLayout();
        var close = (Button)tray.FindName("ResetCloseButton");
        var reset = (Button)tray.FindName("ResetPersonalButton");
        Rect bounds = close.TransformToAncestor(tray).TransformBounds(new Rect(close.RenderSize));
        Assert.True(bounds.Right <= 400);
        Assert.True(close.ActualWidth > 0);
        Assert.True(reset.ActualWidth > 0);
        Loc.SetLanguage("en");
    });

    [Theory]
    [InlineData(0, 600, -12)]
    [InlineData(600, 600, 12)]
    [InlineData(300, 600, 0)]
    [InlineData(-1, 600, 0)]
    [InlineData(601, 600, 0)]
    [InlineData(0, 0, 0)]
    public void DragEdgeScrollingHasDirectionAndStopsOutsideViewport(double x, double width, double speed)
    {
        var method = typeof(ClipboardTray).GetMethod("CategoryDropSpeed", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(speed, (double)method.Invoke(null, new object[] { x, width })!);
    }

    [Fact]
    public void SearchEmptyLabelIncludesQueryAndClearsWithSearch() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        using var tray = new ClipboardTray();
        var search = (TextBox)tray.FindName("SearchBox");
        var empty = (TextBlock)tray.FindName("EmptyText");
        search.Text = "quarterly report";
        tray.ApplyLocalization();
        Assert.Contains("quarterly report", empty.Text);
        Assert.NotEqual(Loc.Get("clipboard.empty"), empty.Text);
        search.Clear();
        tray.ApplyLocalization();
        Assert.Equal(Loc.Get("clipboard.empty"), empty.Text);
    });

    [Fact]
    public void LanguageChangeUpdatesOpenDialogsAndAccessibilityWithoutClearingConfirmation() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        using var tray = new ClipboardTray();
        try
        {
            SetField(tray, "_category", "Personal");
            Invoke(tray, "UpdateCategoryLockState");
            Invoke(tray, "ForgotPin_Click", tray, new RoutedEventArgs());
            var confirmation = (TextBox)tray.FindName("ResetConfirmationBox");
            confirmation.Text = "DELETE";
            SetField(tray, "_deleteConfirmationOpen", true);
            SetField(tray, "_deleteConfirmationCount", 3);
            Loc.SetLanguage("vi");
            tray.ApplyLocalization();

            Assert.Equal("Đặt lại Riêng tư", ((TextBlock)tray.FindName("ResetConfirmationTitle")).Text);
            Assert.Equal(FontWeights.Bold, ((TextBlock)tray.FindName("ResetConfirmationHint")).FontWeight);
            Assert.Equal("DELETE", confirmation.Text);
            Assert.True(((Button)tray.FindName("ResetPersonalButton")).IsEnabled);
            Assert.Equal("PIN của mục Riêng tư", AutomationProperties.GetName((PasswordBox)tray.FindName("PasscodeBox")));
            Assert.Equal("Thông tin clipboard", AutomationProperties.GetName((Button)tray.FindName("InfoButton")));
            Assert.Equal(Loc.Get("clipboard.deleteConfirm", 3), ((TextBlock)tray.FindName("DeleteConfirmationText")).Text);
            var categories = (ItemsControl)tray.FindName("CategoryPanel");
            var personal = Assert.Single(categories.Items.Cast<ClipboardCategoryViewModel>(), item => item.Name == "Personal");
            Assert.Equal("Riêng tư", personal.Label);
            Assert.True(personal.IsSelected);

            Invoke(tray, "ShowInfo", "clipboard.info");
            Loc.SetLanguage("ja");
            tray.ApplyLocalization();
            Assert.Equal(Loc.Get("clipboard.info"), ((TextBox)tray.FindName("DetailText")).Text);
        }
        finally { Loc.SetLanguage("en"); }
    });

    private static SecureString Pin(string digits)
    {
        var pin = new SecureString();
        foreach (char digit in digits) pin.AppendChar(digit);
        pin.MakeReadOnly(); return pin;
    }

    private static byte[] TestImage() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "white-mat-artwork.png"));

    private static CancellationToken LoadToken(object load) =>
        ((CancellationTokenSource)load.GetType().GetProperty("Cancellation")!.GetValue(load)!).Token;
    private static Task LoadCompletion(object load) => (Task)load.GetType().GetProperty("Completion")!.GetValue(load)!;
    private static object? Invoke(ClipboardTray tray, string method, params object?[] args) =>
        typeof(ClipboardTray).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, args);
    private static T Field<T>(ClipboardTray tray, string name) =>
        (T)typeof(ClipboardTray).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
    private static void SetField(ClipboardTray tray, string name, object value) =>
        typeof(ClipboardTray).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tray, value);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static IEnumerable<GeometryDrawing> GeometryDrawings(Drawing drawing) => drawing switch
    {
        GeometryDrawing geometry => [geometry],
        DrawingGroup group => group.Children.SelectMany(GeometryDrawings),
        _ => []
    };
}
