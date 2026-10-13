using System.Diagnostics;
using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ProcessPriorityPolicyTests
{
    [Theory]
    [InlineData("RealTime")]
    [InlineData("realtime")]
    [InlineData("invalid")]
    [InlineData(null)]
    public void UnsupportedPrioritiesResolveToNormal(string? value)
    {
        Assert.Equal("Normal", ProcessPriorityPolicy.Normalize(value));
        Assert.Equal(ProcessPriorityClass.Normal, ProcessPriorityPolicy.Resolve(value));
    }

    [Fact]
    public async Task LoadingLegacyRealtimeSettingsNormalizesAndPersistsTheSafeValue()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vnotch-priority-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"SettingsVersion\": 999, \"ProcessPriority\": \"RealTime\"}");
            await using var service = new SettingsService(path);
            Assert.Equal("Normal", service.Load().ProcessPriority);
            Assert.DoesNotContain("RealTime", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }
}
