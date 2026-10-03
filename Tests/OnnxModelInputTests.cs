using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace VNotch.Tests;

public sealed class OnnxModelInputTests
{
    [Fact]
    public void SmartCropModel_UsesOptimized416InputAndExpectedOutput()
    {
        string modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "yolox_nano.onnx");
        Assert.True(File.Exists(modelPath), $"Model was not copied to {modelPath}");

        using var session = new InferenceSession(modelPath);
        int[] inputDimensions = session.InputMetadata.Single().Value.Dimensions;
        int[] outputDimensions = session.OutputMetadata.Single().Value.Dimensions;

        Assert.Equal(new[] { 1, 3, 416, 416 }, inputDimensions);
        Assert.Equal(new[] { 1, 3549, 85 }, outputDimensions);
        Assert.Equal("c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant());
    }
}
