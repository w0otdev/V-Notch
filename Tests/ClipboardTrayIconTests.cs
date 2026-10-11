using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Controls;
using VNotch.Models;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardTrayIconTests
{
    [Fact]
    public void IconPackLoadsOfflineAndPaintsAtFooterSizes() => SharedStaTestRunner.Run(() =>
    {
        foreach (var kind in Enum.GetValues<TrayAppIconKind>())
        {
            var icon = TrayAppIcons.Get(kind);
            Assert.True(icon.IsFrozen);
            Assert.Same(icon, TrayAppIcons.Get(kind));
            if (icon is DrawingImage vector)
                Assert.True(new Rect(-0.5, -0.5, 65, 65).Contains(vector.Drawing.Bounds), $"{kind} exceeds its viewport");
            else
                Assert.True(Assert.IsAssignableFrom<BitmapSource>(icon).PixelWidth >= 40, $"{kind} has insufficient resolution");
            foreach (int size in new[] { 14, 20, 24 })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) dc.DrawImage(icon, new Rect(0, 0, size, size));
                var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixels = new byte[size * size * 4];
                bitmap.CopyPixels(pixels, size * 4, 0);
                Assert.True(pixels.Where((_, i) => i % 4 == 3).Count(alpha => alpha > 128) > size, $"{kind} is not visible at {size}px");
            }
        }
    });

    [Fact]
    public void UnknownAppsKeepNativeIconsAndNotifyTheFooterWhenLoaded() => SharedStaTestRunner.Run(() =>
    {
        var card = new ClipboardCardViewModel(new ClipboardEntry { SourceApp = "My application", Kind = ClipboardKind.Text });
        var fallback = card.DisplaySourceIcon;
        var changes = new List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var native = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        native.Freeze();
        card.SourceIcon = native;
        Assert.NotSame(fallback, card.DisplaySourceIcon);
        Assert.Same(native, card.DisplaySourceIcon);
        Assert.Contains(nameof(card.DisplaySourceIcon), changes);
        card.UpdateEntry(card.Entry with { SourceApp = "File Explorer", SourceExecutable = "C:\\Windows\\explorer.exe" });
        Assert.Same(TrayAppIcons.Get(TrayAppIconKind.Finder), card.DisplaySourceIcon);
    });

    [Fact]
    public void FileIconsFollowRecycledMetadataAndKnownIconsNeedNoNativeLoad() => SharedStaTestRunner.Run(() =>
    {
        var card = new ClipboardCardViewModel(new ClipboardEntry { Kind = ClipboardKind.File, FileCount = 1, PrimaryExtension = ".pdf" });
        Assert.Same(TrayAppIcons.Get(TrayAppIconKind.Pdf), card.FilePreviewIcon);
        card.UpdateEntry(card.Entry with { PrimaryExtension = ".zip" });
        Assert.Same(TrayAppIcons.Get(TrayAppIconKind.Archive), card.FilePreviewIcon);
        Assert.Null(card.FileIcon);
        card.UpdateEntry(card.Entry with { PrimaryExtension = ".custom" });
        Assert.Same(TrayAppIcons.Get(TrayAppIconKind.Document), card.FilePreviewIcon);
        Assert.Null(card.PreferredFileIcon);
    });
}
