using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightNotchLocaleThumbnailTests
{
    [Fact]
    public void GetRememberedItems_SkipsExcludedFolder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "vnotch-usage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string storePath = Path.Combine(tempRoot, "usage.json");
            string realSecretDir = Path.Combine(tempRoot, "Secret");
            Directory.CreateDirectory(realSecretDir);
            string realSecretDoc = Path.Combine(realSecretDir, "doc.txt");
            File.WriteAllText(realSecretDoc, "secret");

            using var store = new SpotlightUsageStore(storePath, () => DateTime.UtcNow, _ => { });
            store.Preferences.AddExcludedFolder(@"C:\Secret");
            store.Preferences.AddExcludedFolder(realSecretDir);

            var virtualSecretItem = new SpotlightSearchItem(
                @"file:C:\Secret\doc.txt",
                SpotlightResultKind.File,
                "doc.txt",
                @"C:\Secret\doc.txt",
                @"C:\Secret\doc.txt",
                @"C:\Secret\doc.txt");
            var realSecretItem = new SpotlightSearchItem(
                $"file:{realSecretDoc}",
                SpotlightResultKind.File,
                "doc.txt",
                realSecretDoc,
                realSecretDoc,
                realSecretDoc);

            store.RecordLaunch(virtualSecretItem, "doc");
            store.RecordLaunch(realSecretItem, "doc");

            var remembered = store.GetRememberedItems("doc");
            Assert.Empty(remembered);

            var recent = store.GetRecentItems(10);
            Assert.Empty(recent);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetExcludedFoldersSnapshot_CachesSnapshotAndInvalidatesOnChange()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "vnotch-prefs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string prefsPath = Path.Combine(tempRoot, "prefs.json");
            var prefs = new SpotlightPreferences(prefsPath);

            int probeCount = 0;
            prefs.DirectoryExistsProbe = _ =>
            {
                probeCount++;
                return true;
            };

            for (int i = 0; i < 10; i++)
            {
                _ = prefs.GetExcludedFoldersSnapshot();
            }
            Assert.Equal(1, probeCount);

            prefs.AddExcludedFolder(@"C:\Secret");
            for (int i = 0; i < 10; i++)
            {
                string[] snapshot = prefs.GetExcludedFoldersSnapshot();
                Assert.Single(snapshot);
            }
            Assert.Equal(2, probeCount);

            Assert.True(prefs.RemoveExcludedFolder(@"C:\Secret"));
            for (int i = 0; i < 10; i++)
            {
                string[] snapshot = prefs.GetExcludedFoldersSnapshot();
                Assert.Empty(snapshot);
            }
            Assert.Equal(3, probeCount);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NotchManager_UpdatePosition_ComputesGeometryOnceAndFiresSafeAreaChanged()
    {
        var settings = new NotchSettings
        {
            Width = 210,
            Height = 32
        };

        using var manager = new NotchManager(settings);
        Rect? observedSafeArea = null;
        int safeAreaEvents = 0;
        manager.SafeAreaChanged += (_, rect) =>
        {
            safeAreaEvents++;
            observedSafeArea = rect;
        };

        manager.UpdatePosition();

        Assert.Equal(1, manager.GeometryComputeCount);
        Assert.Equal(1, safeAreaEvents);
        Assert.True(observedSafeArea.HasValue);
        Assert.Equal(manager.SafeArea, observedSafeArea.Value);
        Assert.True(manager.SafeArea.Width > 0);
        Assert.True(manager.SafeArea.Height > 0);
    }

    [Fact]
    public void LocalizationService_RejectsOversizedExternalLocaleFile()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "vnotch-locale-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string localeCode = "xx_oversized_" + Guid.NewGuid().ToString("N");
            string oversizedFile = Path.Combine(tempRoot, localeCode + ".json");
            using (var fs = new FileStream(oversizedFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(LocalizationService.MaxExternalLocaleFileBytes + 10);
            }

            Assert.False(LocalizationService.IsValidExternalLocaleFile(oversizedFile));
            Assert.False(Loc.IsValidExternalLocaleFile(oversizedFile));

            LocalizationService.DiscoverExternalDirectory(tempRoot);

            var available = Loc.GetAvailableLanguages();
            Assert.DoesNotContain(available, entry => string.Equals(entry.Code, localeCode, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SmartThumbnailCropService_Analyze_DownscalesAndCopiesPixelsOnce()
    {
        var bitmap = CreateFramedTestBitmap(240, 240);
        ArtworkFrameDetector.RentDownscaledPixelsCallCount = 0;

        Int32Rect? analysisRect = SmartThumbnailCropService.Analyze(bitmap, 120);

        Assert.Equal(1, ArtworkFrameDetector.RentDownscaledPixelsCallCount);
        Assert.True(analysisRect.HasValue);
        Assert.True(analysisRect.Value.Width > 0);
        Assert.True(analysisRect.Value.Height > 0);

        var rectBitmap = CreateFramedTestBitmap(240, 180);
        ArtworkFrameDetector.RentDownscaledPixelsCallCount = 0;
        using var service = new SmartThumbnailCropService();
        Int32Rect? smartRect = service.GetSmartCropRect(rectBitmap, 120);

        Assert.Equal(1, ArtworkFrameDetector.RentDownscaledPixelsCallCount);
        Assert.True(smartRect.HasValue);
        Assert.True(smartRect.Value.Width > 0);
        Assert.True(smartRect.Value.Height > 0);
    }

    private static WriteableBitmap CreateFramedTestBitmap(int width, int height)
    {
        var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        int border = 24;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * stride + x * 4;
                bool isBorder = x < border || x >= width - border || y < border || y >= height - border;
                if (isBorder)
                {
                    pixels[idx + 0] = 8;
                    pixels[idx + 1] = 8;
                    pixels[idx + 2] = 8;
                    pixels[idx + 3] = 255;
                }
                else
                {
                    pixels[idx + 0] = (byte)((x * 3) & 0xFF);
                    pixels[idx + 1] = (byte)((y * 5) & 0xFF);
                    pixels[idx + 2] = 220;
                    pixels[idx + 3] = 255;
                }
            }
        }

        wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        wb.Freeze();
        return wb;
    }
}
