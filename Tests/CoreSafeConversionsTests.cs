using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class CoreSafeConversionsTests
{
    [Fact]
    public void NormalizeAppIcon_CropsTransparentTileWithBreathingRoom_AndHandlesEdgeCases() => SharedStaTestRunner.Run(() =>
    {
        const int size = 100;
        const int stride = size * 4;
        byte[] transparent = new byte[size * stride];
        for (int i = 3; i < transparent.Length; i += 4)
            transparent[i] = 8; // threshold is > 8

        var transparentBmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, transparent, stride);
        transparentBmp.Freeze();
        Assert.Null(AppNotificationService.NormalizeAppIcon(transparentBmp));
        Assert.False(AppNotificationService.TryComputeIconCropBounds(transparent, size, size, stride, out _, out _));

        byte[] centered = new byte[size * stride];
        for (int y = 40; y < 60; y++)
        {
            for (int x = 40; x < 60; x++)
            {
                int idx = y * stride + x * 4;
                centered[idx + 0] = 255;
                centered[idx + 1] = 128;
                centered[idx + 2] = 64;
                centered[idx + 3] = 255;
            }
        }

        Assert.True(AppNotificationService.TryComputeIconCropBounds(centered, size, size, stride, out Int32Rect cropRect, out bool shouldCrop));
        Assert.True(shouldCrop);
        // 20x20 opaque box -> ceil(20 * 0.04) = 1px padding -> [39..60] = 22x22
        Assert.Equal(new Int32Rect(39, 39, 22, 22), cropRect);

        var centeredBmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, centered, stride);
        centeredBmp.Freeze();
        var normalized = Assert.IsAssignableFrom<BitmapSource>(AppNotificationService.NormalizeAppIcon(centeredBmp));
        Assert.True(normalized.IsFrozen);
        Assert.Equal(22, normalized.PixelWidth);
        Assert.Equal(22, normalized.PixelHeight);

        byte[] fullBleed = new byte[64 * 64 * 4];
        for (int i = 3; i < fullBleed.Length; i += 4)
            fullBleed[i] = 255;
        var fullBmp = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, fullBleed, 64 * 4);
        fullBmp.Freeze();
        var uncropped = Assert.IsAssignableFrom<BitmapSource>(AppNotificationService.NormalizeAppIcon(fullBmp));
        Assert.Same(fullBmp, uncropped);
    });

    [Fact]
    public void LyricsProviderOrder_Normalize_PreservesSixProvidersLegacyMigrationAndOrdinalCase()
    {
        string[] expectedDefault =
        [
            "AMLL TTML DB",
            "NetEase",
            "Kugou",
            "QQ Music",
            "LRCLIB",
            "lrc mux"
        ];

        Assert.Equal(expectedDefault, LyricsProviderOrder.Normalize(null));
        Assert.Equal(expectedDefault, LyricsProviderOrder.Normalize(string.Empty));
        Assert.Equal(expectedDefault, LyricsProviderOrder.Normalize("LRCLIB,NetEase,lrc mux"));
        Assert.Equal(expectedDefault, LyricsProviderOrder.Normalize("NetEase,Kugou,QQ Music,LRCLIB,lrc mux"));

        // Case-sensitive Ordinal check rejects lowercase mismatches and falls back to canonical order
        Assert.Equal(expectedDefault, LyricsProviderOrder.Normalize("netease,lrclib,LRC MUX,amll ttml db"));

        string[] custom = LyricsProviderOrder.Normalize("  lrc mux , QQ Music , lrc mux , UnknownProvider ");
        Assert.Equal(
            ["lrc mux", "QQ Music", "AMLL TTML DB", "NetEase", "Kugou", "LRCLIB"],
            custom);
    }

    [Fact]
    public void PerformanceDiagnosticService_NativeProcessAndMemorySampling_ReturnsValidMetrics()
    {
        var service = new PerformanceDiagnosticService(subscribeToRuntimeLog: false);
        long t0 = Stopwatch.GetTimestamp();

        service.SampleThreadAndHandleCounts(t0, out int threads, out int handles);
        Assert.True(threads >= 1);
        Assert.True(handles > 0);

        // Within 2s window, cached thread count path still reports valid counts
        service.SampleThreadAndHandleCounts(t0 + Stopwatch.Frequency / 2, out int cachedThreads, out int handles2);
        Assert.Equal(threads, cachedThreads);
        Assert.True(handles2 > 0);

        PerformanceDiagnosticService.SampleGlobalMemory(
            out ulong ramUsed,
            out ulong ramTotal,
            out ulong ramAvail,
            out double ramPercent);

        Assert.True(ramTotal > 0);
        Assert.True(ramTotal >= ramAvail);
        Assert.Equal(ramTotal - ramAvail, ramUsed);
        Assert.InRange(ramPercent, 0.0, 100.0);
    }

    [Fact]
    public void BackupNamePathPolicyCanvasUriAndBluetoothPercentage_ParityChecks()
    {
        Assert.True(SettingsService.IsGeneratedSettingsBackup(@"C:\AppData\V-Notch\settings.bak-20260228-235959-999.json"));
        Assert.True(SettingsService.IsGeneratedSettingsBackup(@"settings.bak-20240229-000000-000-0123456789abcdefABCDEF0123456789.json"));
        Assert.False(SettingsService.IsGeneratedSettingsBackup(@"settings.bak-20230229-000000-000.json")); // 2023 not leap year
        Assert.False(SettingsService.IsGeneratedSettingsBackup(@"SETTINGS.bak-20260228-120000-000.json")); // Ordinal case

        Assert.True(FileIconProvider.CanProbeLocalFilesystemPath(@"C:\Windows\System32\mmc.exe"));
        Assert.True(FileIconProvider.CanProbeLocalFilesystemPath("  d:/tools/console.msc  "));
        Assert.False(FileIconProvider.CanProbeLocalFilesystemPath(@"\\server\share\console.msc"));
        Assert.False(FileIconProvider.CanProbeLocalFilesystemPath("https://example.com/console.msc"));
        Assert.False(FileIconProvider.CanProbeLocalFilesystemPath("relative\\console.msc"));

        Assert.True(SpotifyCanvasProtocol.TryCreateCanvasUri("https://canvaz.scdn.co/upload/artist/video.mp4?token=1", out Uri? canvasUri));
        Assert.NotNull(canvasUri);
        Assert.False(SpotifyCanvasProtocol.TryCreateCanvasUri("http://canvaz.scdn.co/upload/artist/video.mp4", out _));
        Assert.False(SpotifyCanvasProtocol.TryCreateCanvasUri("https://evil-scdn.co/upload/artist/video.mp4", out _));
        Assert.False(SpotifyCanvasProtocol.TryCreateCanvasUri("https://canvaz.scdn.co/upload/artist/image.jpg?ext=.mp4", out _));

        Assert.Equal(-1, BluetoothBatteryReader.ParsePercentage(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, BluetoothBatteryReader.ParsePercentage([0]));
        Assert.Equal(100, BluetoothBatteryReader.ParsePercentage([100]));
        Assert.Equal(-1, BluetoothBatteryReader.ParsePercentage([101]));
        Assert.Equal(-1, BluetoothBatteryReader.ParsePercentage([50, 0]));
    }
}
