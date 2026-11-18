using System.IO;
using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed partial class SmartCropRuntimeLifetimeTests
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_smartcrop_session_create(string model, int modelLength,
        string directory, int directoryLength, byte preferDml, out long handle, out byte dml);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_session_destroy(long handle);

    [DllImport("VNotch.Native.dll")]
    private static extern int vn_smartcrop_session_infer(long handle, byte[] pixels,
        int length, int width, int height, int stride, [Out] SmartThumbnailCropService.Detection[] detections,
        int capacity, out int count, out byte dml);

    [Fact]
    public void RuntimeSurvivesAnotherSessionsDisposalAndCanReloadAfterTheLastSession()
    {
        const int width = 160, height = 96, stride = width * 4;
        var pixels = new byte[height * stride];
        new Random(42).NextBytes(pixels);
        var detections = new SmartThumbnailCropService.Detection[3549];
        int? previousCount = null;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            long first = 0, second = 0;
            try
            {
                first = CreateSession();
                second = CreateSession();
                Assert.Equal(0, vn_smartcrop_session_destroy(first));
                first = 0;
                Assert.Equal(0, vn_smartcrop_session_infer(second, pixels, pixels.Length, width, height,
                    stride, detections, detections.Length, out int count, out byte dml));
                Assert.Equal(0, dml);
                if (previousCount.HasValue) Assert.Equal(previousCount.Value, count);
                previousCount = count;
            }
            finally
            {
                if (first != 0) vn_smartcrop_session_destroy(first);
                if (second != 0) vn_smartcrop_session_destroy(second);
            }
        }
    }

    [Fact]
    public void FailedSessionConstructionDoesNotPoisonTheNextRuntimeLoad()
    {
        string directory = AppContext.BaseDirectory;
        string missingModel = Path.Combine(directory, "Models", "missing-memory-test.onnx");
        Assert.NotEqual(0, vn_smartcrop_session_create(missingModel, missingModel.Length,
            directory, directory.Length, 0, out long invalid, out _));
        Assert.Equal(0, invalid);
        long valid = CreateSession();
        Assert.Equal(0, vn_smartcrop_session_destroy(valid));
    }

    private static long CreateSession()
    {
        string directory = AppContext.BaseDirectory;
        string model = Path.Combine(directory, "Models", "yolox_nano.onnx");
        Assert.True(File.Exists(model));
        Assert.Equal(0, vn_smartcrop_session_create(model, model.Length,
            directory, directory.Length, 0, out long handle, out byte dml));
        Assert.NotEqual(0, handle);
        Assert.Equal(0, dml);
        return handle;
    }
}
