using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed partial class StrixSecurityAuditRegressionTests
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_crash_reporter_format_duration(
        double totalDays,
        double totalHours,
        double totalMinutes,
        double totalSeconds,
        int hours,
        int minutes,
        int seconds,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_version_format_assembly(
        byte hasVersion,
        int major,
        int minor,
        int build,
        int revision,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_spotlight_ai_resolve_provider_error(
        int code,
        byte streaming,
        Span<char> outKeyBuf,
        int keyCap,
        out int outKeyLen,
        Span<char> outDiagBuf,
        int diagCap,
        out int outDiagLen);

    [LibraryImport(NativeDll)]
    private static partial int vn_media_artwork_detect_content_bounds(
        ReadOnlySpan<byte> pixels,
        int pixelsLen,
        int stride,
        int sw,
        int sh,
        int width,
        int height,
        double scaleX,
        double scaleY,
        out int outX,
        out int outY,
        out int outW,
        out int outH);

    [LibraryImport(NativeDll)]
    private static partial int vn_glass_upload_delta_copy_changed_rows(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int destinationStride,
        int rowBytes,
        int height,
        out int outTop,
        out int outBottom);

    [LibraryImport(NativeDll)]
    private static partial int vn_color_extract_sampled(
        ReadOnlySpan<byte> pixels,
        int pixelLen,
        int width,
        int height,
        ReadOnlySpan<double> coords,
        int coordsLen,
        out byte outR,
        out byte outG,
        out byte outB);

    [LibraryImport(NativeDll)]
    private static partial int vn_subject_blur_brighten(
        Span<byte> pixels,
        int bufLen,
        float factor);

    [LibraryImport(NativeDll)]
    private static partial int vn_subject_blur_composite(
        Span<byte> background,
        ReadOnlySpan<byte> subjectLayer,
        float centerX,
        float centerY,
        float subWidth,
        float subHeight,
        int width,
        int height,
        int stride);

    [LibraryImport(NativeDll)]
    private static partial int vn_artwork_frame_detect_any(
        ReadOnlySpan<byte> pixels,
        int w,
        int h,
        int width,
        int height,
        out int outX,
        out int outY,
        out int outW,
        out int outH,
        out byte outFound);

    [LibraryImport(NativeDll)]
    private static partial int vn_clipboard_vault_validate_encrypted_attachment_length(
        long plainLength,
        long streamLength,
        int chunkSize,
        out byte outValid);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_weather_service_clean_city(
        ReadOnlySpan<char> city,
        int cityLen,
        Span<char> outBuf,
        int outCap,
        out int outLen,
        out byte outHasValue);

    [Fact]
    public void Vuln01_ExtractArchiveDirectory_RejectsZipSlipPathTraversal()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "vnotch-strix-" + Guid.NewGuid().ToString("N"));
        string destDir = Path.Combine(tempRoot, "dest");
        Directory.CreateDirectory(destDir);
        try
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("blocked");
            }
            ms.Position = 0;

            Assert.Throws<IOException>(() =>
                ClipboardHistoryStore.ExtractArchiveDirectory(ms, destDir, lease: null));
            Assert.False(File.Exists(Path.Combine(tempRoot, "escaped.txt")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Vuln01_ExtractArchiveDirectory_RejectsZipBombQuotaAndEntryLimit()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "vnotch-strix-bomb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry1 = zip.CreateEntry("file1.bin");
                using (var s = entry1.Open())
                    s.Write(new byte[2048]);

                var entry2 = zip.CreateEntry("file2.bin");
                using (var s = entry2.Open())
                    s.Write(new byte[2048]);
            }

            ms.Position = 0;
            Assert.Throws<IOException>(() =>
                ClipboardHistoryStore.ExtractArchiveDirectory(ms, Path.Combine(tempRoot, "q1"), lease: null, maxTotalBytes: 3000, maxEntries: 10));

            ms.Position = 0;
            Assert.Throws<IOException>(() =>
                ClipboardHistoryStore.ExtractArchiveDirectory(ms, Path.Combine(tempRoot, "q2"), lease: null, maxTotalBytes: 100_000, maxEntries: 1));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Vuln02_CrashReporterAndVersionFormatting_NeverOverreadStackBuffer()
    {
        Span<char> buf = stackalloc char[128];

        int rc = vn_crash_reporter_format_duration(0.0, 0.0, 0.0, 1e100, 0, 0, 0, buf, buf.Length, out int len);
        Assert.Equal(0, rc);
        Assert.InRange(len, 1, 63);
        string text = new(buf[..len]);
        Assert.EndsWith("s", text, StringComparison.Ordinal);

        rc = vn_crash_reporter_format_duration(double.NaN, double.PositiveInfinity, -1.0, double.NaN, 0, 0, 0, buf, buf.Length, out len);
        Assert.Equal(0, rc);
        Assert.Equal("0.0s", new string(buf[..len]));

        rc = vn_app_version_format_assembly(1, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, buf, buf.Length, out len);
        Assert.Equal(0, rc);
        Assert.InRange(len, 1, 63);

        Span<char> keyBuf = stackalloc char[64];
        Span<char> diagBuf = stackalloc char[64];
        rc = vn_spotlight_ai_resolve_provider_error(int.MaxValue, 1, keyBuf, keyBuf.Length, out int keyLen, diagBuf, diagBuf.Length, out int diagLen);
        Assert.Equal(0, rc);
        Assert.InRange(keyLen, 1, 63);
        Assert.InRange(diagLen, 1, 31);
    }

    [Fact]
    public void Vuln03_PixelAndStrideExports_RejectInvalidStridesAndNonFiniteInputs()
    {
        byte[] pixels = new byte[16 * 16 * 4];

        // Stride smaller than sw * 4 or negative must return VN_ERR_INVALID_ARG
        Assert.True(vn_media_artwork_detect_content_bounds(pixels, pixels.Length, 4, 16, 16, 100, 100, 1.0, 1.0, out _, out _, out _, out _) < 0);
        Assert.True(vn_media_artwork_detect_content_bounds(pixels, pixels.Length, -64, 16, 16, 100, 100, 1.0, 1.0, out _, out _, out _, out _) < 0);

        // Non-finite or zero scale_x / scale_y must return VN_ERR_INVALID_ARG
        Assert.True(vn_media_artwork_detect_content_bounds(pixels, pixels.Length, 64, 16, 16, 100, 100, 0.0, 1.0, out _, out _, out _, out _) < 0);
        Assert.True(vn_media_artwork_detect_content_bounds(pixels, pixels.Length, 64, 16, 16, 100, 100, double.NaN, 1.0, out _, out _, out _, out _) < 0);

        // GlassUploadDelta must reject stride < row_bytes
        byte[] dst = new byte[pixels.Length];
        Assert.True(vn_glass_upload_delta_copy_changed_rows(pixels, 16, dst, 64, 64, 16, out _, out _) < 0);
        Assert.True(vn_glass_upload_delta_copy_changed_rows(pixels, 64, dst, -64, 64, 16, out _, out _) < 0);

        // ColorExtractionService must ignore NaN/Inf/out-of-range coordinates safely
        double[] coords = [double.NaN, 0.5, 0.5, double.PositiveInfinity, -0.5, 0.5, 1.0, 1.0, 0.5, 0.5];
        Assert.Equal(0, vn_color_extract_sampled(pixels, pixels.Length, 16, 16, coords, coords.Length, out _, out _, out _));

        // SubjectAwareBlurService must handle NaN factor/coordinates and reject stride < width * 4
        Assert.Equal(0, vn_subject_blur_brighten( dst, dst.Length, float.NaN));
        Assert.True(vn_subject_blur_composite(dst, pixels, 0.5f, 0.5f, 0.5f, 0.5f, 16, 16, 16) < 0);
        Assert.Equal(0, vn_subject_blur_composite(dst, pixels, float.NaN, float.PositiveInfinity, -1.0f, float.NaN, 16, 16, 64));

        // ArtworkFrameDetector must reject dimensions smaller than 8x8
        Assert.True(vn_artwork_frame_detect_any(pixels, 4, 4, 100, 100, out _, out _, out _, out _, out _) < 0);
    }

    [Fact]
    public void Vuln04_ClipboardVaultValidateLength_PreventsSigned64BitOverflow()
    {
        int rc = vn_clipboard_vault_validate_encrypted_attachment_length(long.MaxValue - 5, 44, 65536, out byte valid);
        Assert.Equal(0, rc);
        Assert.Equal(0, valid);

        rc = vn_clipboard_vault_validate_encrypted_attachment_length(long.MaxValue, long.MaxValue, 1, out valid);
        Assert.Equal(0, rc);
        Assert.Equal(0, valid);

        rc = vn_clipboard_vault_validate_encrypted_attachment_length(100, 16 + 100 + 28, 65536, out valid);
        Assert.Equal(0, rc);
        Assert.Equal(1, valid);
    }

    [Fact]
    public void Vuln06_WeatherServiceCleanCity_RejectsControlCharOnlyInput()
    {
        Span<char> outBuf = stackalloc char[64];
        string controlOnly = "\u0001\u0002\u007F \t";
        int rc = vn_weather_service_clean_city(controlOnly.AsSpan(), controlOnly.Length, outBuf, outBuf.Length, out int len, out byte hasValue);
        Assert.Equal(0, rc);
        Assert.Equal(0, hasValue);
        Assert.Equal(0, len);

        string cityWithControl = "  Hanoi\u0007 ";
        rc = vn_weather_service_clean_city(cityWithControl.AsSpan(), cityWithControl.Length, outBuf, outBuf.Length, out len, out hasValue);
        Assert.Equal(0, rc);
        Assert.Equal(1, hasValue);
        Assert.Equal("Hanoi", new string(outBuf[..len]));
    }
}
