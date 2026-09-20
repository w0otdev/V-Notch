using System.Numerics;
using VNotch.Benchmarks;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public sealed class GlassBoxBlurTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(255)]
    [InlineData(601)]
    [InlineData(4097)]
    public void AverageIsExactForEveryPossibleChannelSum(int window)
    {
        var average = new GlassBoxBlur.ExactByteAverage(window);
        for (int sum = 0; sum <= 255 * window; sum++)
            Assert.Equal((byte)(sum / window), average.Divide(sum));

        var divisor = new Vector<float>(window);
        int[] lanes = new int[Vector<int>.Count];
        for (int sum = 0; sum <= 255 * window; sum += lanes.Length)
        {
            for (int lane = 0; lane < lanes.Length; lane++)
                lanes[lane] = Math.Min(sum + lane, 255 * window);
            var result = GlassBoxBlur.Average(new Vector<int>(lanes), divisor);
            for (int lane = 0; lane < lanes.Length; lane++)
                Assert.Equal(lanes[lane] / window, result[lane]);
        }
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 3, 1)]
    [InlineData(63, 17, 8)]
    [InlineData(64, 19, 9)]
    [InlineData(65, 21, 10)]
    [InlineData(127, 67, 20)]
    [InlineData(515, 280, 20)]
    [InlineData(1120, 1020, 8)]
    [InlineData(65, 3, 2050)]
    public void ThreePassesMatchOriginalByteForByteIncludingPartitionBoundaries(int w, int h, int radius)
    {
        byte[] expected = new byte[w * h * 4];
        new Random(42).NextBytes(expected);
        byte[] actual = (byte[])expected.Clone();
        byte[] oldScratch = new byte[expected.Length], newScratch = new byte[expected.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BaselineBoxBlur.BoxBlurHorizontal(expected, oldScratch, w, radius, 0, h);
            GlassBoxBlur.Horizontal(actual, newScratch, w, radius, 0, h / 2);
            GlassBoxBlur.Horizontal(actual, newScratch, w, radius, h / 2, h);
            Assert.True(oldScratch.AsSpan().SequenceEqual(newScratch), "Horizontal pixels differ");
            BaselineBoxBlur.BoxBlurVertical(oldScratch, expected, w, h, radius, 0, w);
            Parallel.Invoke(
                () => GlassBoxBlur.Vertical(newScratch, actual, w, h, radius, 0, w / 2),
                () => GlassBoxBlur.Vertical(newScratch, actual, w, h, radius, w / 2, w));
            Assert.True(expected.AsSpan().SequenceEqual(actual), "Vertical pixels differ");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void BlurPreservesConstantChannelsAtAllEdges(byte value)
    {
        const int w = 131, h = 79;
        byte[] pixels = Enumerable.Repeat(value, w * h * 4).ToArray();
        byte[] scratch = new byte[pixels.Length], output = new byte[pixels.Length];
        GlassBoxBlur.Horizontal(pixels, scratch, w, 20, 0, h);
        GlassBoxBlur.Vertical(scratch, output, w, h, 20, 0, w);
        Assert.True(pixels.AsSpan().SequenceEqual(output));
    }
}
