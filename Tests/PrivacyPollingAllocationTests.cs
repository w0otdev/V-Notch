using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class PrivacyPollingAllocationTests
{
    [Fact]
    public void ConsumerNamesPreserveCaseInsensitiveDistinctOrdering()
    {
        Assert.Same(Array.Empty<string>(),
            PrivacyIndicatorService.GetConsumerNames(Array.Empty<CapabilityUsage>()));
        Assert.Equal(new[] { "Recorder" }, PrivacyIndicatorService.GetConsumerNames(
            new[] { new CapabilityUsage("one", "Recorder", 10) }));
        Assert.Equal(new[] { "Alpha", "Zulu" }, PrivacyIndicatorService.GetConsumerNames(
            new[]
            {
                new CapabilityUsage("one", "Zulu", 10),
                new CapabilityUsage("two", "Alpha", 20),
                new CapabilityUsage("three", "ALPHA", 30)
            }));
    }

    [Fact]
    public void MicrophoneCandidateChangesTrackOrderCountAndRawName()
    {
        string[] cached = Array.Empty<string>();
        var candidates = new[]
        {
            new CapabilityUsage("First", "Recorder", 10),
            new CapabilityUsage("Second", "Recorder", 20)
        };
        Assert.True(PrivacyIndicatorService.UpdateMicrophoneCandidateNames(candidates, ref cached));
        Assert.Equal(new[] { "First", "Second" }, cached);

        var original = cached;
        var equivalent = new[]
        {
            new CapabilityUsage("FIRST", "Renamed", 30),
            new CapabilityUsage("SECOND", "Renamed", 40)
        };
        Assert.False(PrivacyIndicatorService.UpdateMicrophoneCandidateNames(equivalent, ref cached));
        Assert.Same(original, cached);

        Array.Reverse(candidates);
        Assert.True(PrivacyIndicatorService.UpdateMicrophoneCandidateNames(candidates, ref cached));
        Assert.Equal(new[] { "Second", "First" }, cached);
        Assert.True(PrivacyIndicatorService.UpdateMicrophoneCandidateNames(
            new[] { new CapabilityUsage("Third", "Recorder", 10) }, ref cached));
        Assert.Equal(new[] { "Third" }, cached);
        Assert.True(PrivacyIndicatorService.UpdateMicrophoneCandidateNames(
            new[] { new CapabilityUsage("Fourth", "Recorder", 10) }, ref cached));
        Assert.Equal(new[] { "Fourth" }, cached);
        Assert.True(PrivacyIndicatorService.UpdateMicrophoneCandidateNames(
            Array.Empty<CapabilityUsage>(), ref cached));
        Assert.Same(Array.Empty<string>(), cached);
        Assert.False(PrivacyIndicatorService.UpdateMicrophoneCandidateNames(
            Array.Empty<CapabilityUsage>(), ref cached));
    }

    [Fact]
    public void UnchangedMicrophoneCandidatesAndEmptyConsumerNamesDoNotAllocate()
    {
        var candidates = new[] { new CapabilityUsage("Recorder", "Recorder", 10) };
        string[] cached = Array.Empty<string>();
        PrivacyIndicatorService.UpdateMicrophoneCandidateNames(candidates, ref cached);
        var empty = Array.Empty<CapabilityUsage>();
        for (int i = 0; i < 100; i++)
        {
            PrivacyIndicatorService.UpdateMicrophoneCandidateNames(candidates, ref cached);
            PrivacyIndicatorService.GetConsumerNames(empty);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            PrivacyIndicatorService.UpdateMicrophoneCandidateNames(candidates, ref cached);
            PrivacyIndicatorService.GetConsumerNames(empty);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
