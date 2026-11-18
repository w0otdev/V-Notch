using System.IO;
using System.Text.Json;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

public sealed class AgentOrbTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(40)]
    public void EveryParticleMatchesTheDetailsDemo(double size)
    {
        // Captured from the actual Details Orb 01 JS painter, 40px interpolated
        // presets, at three independent instants (including reverse solve moves).
        using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "agent-orb-details-golden.json")));
        foreach (var frame in reference.RootElement.EnumerateArray())
        {
            var state = Enum.Parse<ThinkingOrbState>(frame.GetProperty("state").GetString()!, true);
            var expected = frame.GetProperty("dots");
            var actual = new ThinkingOrbGeometry.Dot[AgentOrbGeometry.Capacity];
            int count = AgentOrbGeometry.WriteFrame(actual, state, size, frame.GetProperty("seconds").GetDouble());
            Assert.Equal(expected.GetArrayLength(), count);
            for (int i = 0; i < count; i++)
            {
                var dot = actual[i];
                double scale = 40 / size;
                double[] values = [dot.X * scale, dot.Y * scale, dot.Z, dot.Radius * scale, dot.Ink, dot.Alpha];
                for (int j = 0; j < values.Length; j++)
                    Assert.InRange(Math.Abs(values[j] - expected[i][j].GetDouble()), 0, 1e-6);
            }
        }
    }

    [Fact]
    public void RandomCycleIncludesAllFourStatesAndNeverRepeatsAcrossBags()
    {
        var sequence = new AgentOrbSequence(new Random(420));
        ThinkingOrbState? previous = null;
        var orders = new HashSet<string>();
        for (int cycle = 0; cycle < 100; cycle++)
        {
            var bag = new List<ThinkingOrbState>();
            for (int i = 0; i < 4; i++)
            {
                var state = sequence.Next();
                Assert.NotEqual(previous, state);
                Assert.Contains(state, new[] { ThinkingOrbState.Searching, ThinkingOrbState.Shaping,
                    ThinkingOrbState.Listening, ThinkingOrbState.Solving });
                bag.Add(state);
                previous = state;
            }
            Assert.Equal(4, bag.Distinct().Count());
            orders.Add(string.Join(",", bag));
        }
        Assert.True(orders.Count > 10);
    }
}
