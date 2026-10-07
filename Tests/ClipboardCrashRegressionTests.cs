using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardCrashRegressionTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void MenuHitTestingHandlesAttachedAndDetachedVisuals() => SharedStaTestRunner.Run(() =>
    {
        var visual = new Border();
        var window = new BackgroundWindow { Content = visual, Width = 120, Height = 80 };
        try
        {
            window.Show(); window.UpdateLayout();
            Point screen = visual.PointToScreen(new Point(10, 10));
            Assert.True(ContainsVisualPoint(visual, screen));
            window.Content = null;
            Assert.Null(PresentationSource.FromVisual(visual));
            Assert.Throws<InvalidOperationException>(() => visual.PointFromScreen(screen));
            Assert.False(ContainsVisualPoint(visual, screen));
            Assert.False(ContainsVisualPoint(new ContextMenu(), screen));
            Assert.False(ContainsVisualPoint(new MenuItem(), screen));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void EmptyTextPreservesRichClipboardFormats(string text) => SharedStaTestRunner.Run(() =>
    {
        var data = new DataObject();
        var content = new ClipboardContent { Text = text, Html = "<b>rich only</b>", Rtf = @"{\rtf1 rich only}" };
        typeof(ClipboardHistoryController).GetMethod("SetTextFormats", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [data, content]);
        Assert.Equal(text, data.GetData(DataFormats.UnicodeText));
        Assert.Equal(content.Html, data.GetData(DataFormats.Html));
        Assert.Equal(content.Rtf, data.GetData(DataFormats.Rtf));
    });

    [Fact]
    public void EmptySelectionTextStillExportsFiles() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        await store.InitializeAsync();
        string source = Path.Combine(fixture.Root, "file.txt");
        File.WriteAllText(source, "file bytes");
        var rich = (await store.ImportAsync(new ClipboardCapture { Html = "<b>no plain text</b>" })).Entry!;
        var file = (await store.ImportAsync(new ClipboardCapture { FilePaths = [source] })).Entry!;
        var paths = new List<string>();
        var pending = (Task<(DataObject Data, bool Personal)>)typeof(ClipboardHistoryController)
            .GetMethod("CreateSelectionDataAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(controller, [new[] { rich, file }, paths])!;
        var result = await pending;
        Assert.Equal("", result.Data.GetData(DataFormats.UnicodeText));
        Assert.Equal("file bytes", File.ReadAllText(Assert.Single(paths)));
        Assert.Equal(paths, result.Data.GetFileDropList().Cast<string>());
        Assert.False(result.Personal);
    });

    [Theory]
    [InlineData("Ctrl+DefinitelyNotAKey")]
    [InlineData("Alt+999999")]
    [InlineData("Win+UnknownMediaKey")]
    public void UnknownHotkeysReturnFalse(string gesture)
        => Assert.False(ClipboardHotkeyController.TryParse(gesture, out _, out _));

    [Fact]
    public void ValidHotkeyKeepsItsVirtualKeyAndModifiers()
    {
        Assert.True(ClipboardHotkeyController.TryParse("Ctrl+Shift+V", out uint modifiers, out uint key));
        Assert.Equal(6u, modifiers);
        Assert.Equal(0x56u, key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageBatchGetsPreviewAndKeepsEveryOriginalWithoutScreenshotDeduplication(bool batchFirst)
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        string[] files = Enumerable.Range(0, 3).Select(i => Path.Combine(fixture.Root, $"{i}.png")).ToArray();
        byte[] corrupt = [137, 80, 78, 71, 0];
        File.WriteAllBytes(files[0], corrupt);
        File.WriteAllBytes(files[1], Png); File.WriteAllBytes(files[2], Png);
        var batch = new ClipboardCapture { FilePaths = files, SourceApp = "SnippingTool" };
        var single = new ClipboardCapture { ImagePng = Png, SourceApp = "SnippingTool" };
        var first = (await store.ImportAsync(batchFirst ? batch : single)).Entry!;
        var second = (await store.ImportAsync(batchFirst ? single : batch)).Entry!;
        var entry = batchFirst ? first : second;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ClipboardKind.Image, entry.Kind);
        Assert.NotNull(await store.GetImageAsync(entry.Id));
        Assert.Equal(3, (await store.GetContentAsync(entry.Id)).Files.Length);
        Assert.Equal(2, store.Snapshot().Count);
        using var pin = Pin("123456");
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        await store.MovePersonalAsync(entry.Id, true);
        Assert.NotNull(await store.GetImageAsync(entry.Id));
        await store.MovePersonalAsync(entry.Id, false);
        var exported = await store.ExportFilesAsync(entry.Id);
        Assert.Equal(3, exported.Length);
        Assert.Equal(corrupt, File.ReadAllBytes(exported[0]));
        Assert.Equal(Png, File.ReadAllBytes(exported[1]));
        Assert.Equal(Png, File.ReadAllBytes(exported[2]));
    }

    [Fact]
    public async Task UndecodableImageBatchUsesFileCard()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        string[] files = Enumerable.Range(0, 3).Select(i => Path.Combine(fixture.Root, $"broken-{i}.png")).ToArray();
        foreach (string file in files) File.WriteAllText(file, "broken image");
        var entry = (await store.ImportAsync(new ClipboardCapture { FilePaths = files })).Entry!;
        Assert.Equal(ClipboardKind.File, entry.Kind);
        Assert.Null(await store.GetImageAsync(entry.Id));
        Assert.Equal(3, (await store.ExportFilesAsync(entry.Id)).Length);
    }

    [Fact]
    public void VaultDistinguishesCooldownAndDoesNotExtendItForBlockedAttempts()
    {
        using var fixture = new Fixture();
        DateTime now = DateTime.UtcNow;
        using var vault = new ClipboardVault(fixture.Root, () => now);
        using var correct = Pin("123456"); using var wrong = Pin("654321");
        Assert.True(vault.Unlock(correct, create: true));
        vault.Lock();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var failed = vault.UnlockWithResult(wrong, create: false);
            Assert.Equal(ClipboardUnlockStatus.InvalidPin, failed.Status);
            Assert.Equal(now.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, attempt)))), failed.RetryAfterUtc);
            for (int retry = 0; retry < 3; retry++)
            {
                var limited = vault.UnlockWithResult(correct, create: false);
                Assert.Equal(ClipboardUnlockStatus.RateLimited, limited.Status);
                Assert.Equal(failed.RetryAfterUtc, limited.RetryAfterUtc);
                Assert.False(vault.IsUnlocked);
            }
            now = failed.RetryAfterUtc;
        }
        Assert.True(vault.UnlockWithResult(correct, create: false).Succeeded);
        vault.Lock();
        Assert.Equal(now.AddSeconds(1), vault.UnlockWithResult(wrong, create: false).RetryAfterUtc);
        vault.Reset();
        Assert.True(vault.Unlock(correct, create: true));
    }

    [Fact]
    public void TrayShowsRetryDelayForCorrectPinDuringCooldown() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        await store.InitializeAsync();
        using var pin = Pin("123456");
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        await store.LockPersonalAsync();
        tray.Attach(controller);
        var vault = (ClipboardVault)typeof(ClipboardHistoryStore).GetField("_vault", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        DateTime deadline = DateTime.UtcNow.AddMinutes(1);
        typeof(ClipboardVault).GetField("_retryAfterUtc", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vault, deadline);
        ((PasswordBox)tray.FindName("PasscodeBox")).Password = "123456";
        await (Task)typeof(ClipboardTray).GetMethod("UnlockPersonalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null)!;
        string hint = ((TextBlock)tray.FindName("LockHint")).Text;
        Assert.NotEqual(Loc.Get("clipboard.wrongPin"), hint);
        Assert.Matches("[0-9]+", hint);
        Assert.False(store.IsPersonalUnlocked);
        Assert.Equal(deadline, (await store.UnlockPersonalWithResultAsync(pin)).RetryAfterUtc);
    });

    [Fact]
    public void FileTransformsReplaceInterruptedOutputAndPreserveDestinationOnFailure()
    {
        using var fixture = new Fixture();
        using var vault = new ClipboardVault(fixture.Root);
        using var pin = Pin("123456");
        Assert.True(vault.Unlock(pin, create: true));
        byte[] original = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 79);
        string source = Path.Combine(fixture.Root, "original.bin"), encrypted = source + ".enc", restored = Path.Combine(fixture.Root, "restored.bin");
        File.WriteAllBytes(source, original);
        File.WriteAllBytes(encrypted, new byte[original.Length * 2]);
        File.WriteAllText(encrypted + ".tmp", "interrupted encryption");
        vault.TransformFile(source, encrypted, encrypt: true);
        File.WriteAllBytes(restored, new byte[original.Length * 2]);
        File.WriteAllText(restored + ".tmp", "interrupted decryption");
        vault.TransformFile(encrypted, restored, encrypt: false);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(original, File.ReadAllBytes(restored));
        byte[] tampered = File.ReadAllBytes(encrypted); tampered[^1] ^= 1; File.WriteAllBytes(encrypted, tampered);
        Assert.Throws<AuthenticationTagMismatchException>(() => vault.TransformFile(encrypted, restored, encrypt: false));
        Assert.Equal(original, File.ReadAllBytes(restored));
        Assert.False(File.Exists(restored + ".tmp"));
        Assert.False(File.Exists(encrypted + ".tmp"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PersonalMovesHandleExistingDestinationAndRollbackOnSourceDeleteFailure(bool startPersonal, bool blockDelete)
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.History);
        await store.InitializeAsync();
        string source = Path.Combine(fixture.Root, "attachment.txt");
        File.WriteAllText(source, "preserved attachment");
        var entry = (await store.ImportAsync(new ClipboardCapture { Text = "preserved text", FilePaths = [source] })).Entry!;
        using var pin = Pin("123456");
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        if (startPersonal) await store.MovePersonalAsync(entry.Id, true);
        string original = Path.Combine(fixture.History, startPersonal ? "personal" : "items", entry.Id.ToString("N"));
        string destination = Path.Combine(fixture.History, startPersonal ? "items" : "personal", entry.Id.ToString("N"));
        Directory.CreateDirectory(destination);
        string remnant = Path.Combine(destination, "stale.bin");
        File.WriteAllText(remnant, "previous incomplete move");
        using (var reader = blockDelete ? new FileStream(Path.Combine(original, startPersonal ? "0.bin.enc" : "0.bin"), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            if (blockDelete)
            {
                await Assert.ThrowsAsync<IOException>(() => store.MovePersonalAsync(entry.Id, !startPersonal));
                Assert.Equal(startPersonal, store.GetEntry(entry.Id)!.IsPersonal);
                Assert.Equal("previous incomplete move", File.ReadAllText(remnant));
                Assert.True(Directory.Exists(original));
            }
            else
            {
                await store.MovePersonalAsync(entry.Id, !startPersonal);
                Assert.Equal(!startPersonal, store.GetEntry(entry.Id)!.IsPersonal);
                Assert.False(Directory.Exists(original));
                Assert.False(File.Exists(remnant));
            }
        }
        Assert.Equal("preserved text", (await store.GetContentAsync(entry.Id)).Text);
        if (blockDelete) await store.MovePersonalAsync(entry.Id, !startPersonal);
        await store.MovePersonalAsync(entry.Id, false);
        Assert.Equal("preserved attachment", File.ReadAllText(Assert.Single(await store.ExportFilesAsync(entry.Id))));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.History, "staging")));
    }

    private static bool ContainsVisualPoint(FrameworkElement visual, Point point)
        => (bool)typeof(ClipboardTray).GetMethod("ContainsVisualPoint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [visual, point])!;

    private static SecureString Pin(string digits)
    {
        var pin = new SecureString();
        foreach (char digit in digits) pin.AppendChar(digit);
        pin.MakeReadOnly(); return pin;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-clipboard-crash-" + Guid.NewGuid().ToString("N"));
        internal string History => Path.Combine(Root, "history");
        internal Fixture() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
