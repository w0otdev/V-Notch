using System.IO;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class SpotlightWindowFixtureTests
{
    [Fact]
    public void FixtureDrainsPendingWritesAndRemovesItsDirectoryAfterFailure() => SharedStaTestRunner.Run(() =>
    {
        string? directory = null;
        var item = new SpotlightSearchItem("fixture:app", SpotlightResultKind.Application,
            "Fixture app", "", "shell:AppsFolder\\Fixture!App");
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var fixture = new SpotlightWindowFixture(new NotchSettings { NotchStyle = "default" });
            directory = fixture.DirectoryPath;
            fixture.Usage.RecordLaunch(item);
            fixture.Usage.WaitForPendingSavesAsync().GetAwaiter().GetResult();
            Assert.True(File.Exists(fixture.UsagePath));
            fixture.Usage.RecordLaunch(item); // Disposal must also drain this newer write.
            throw new InvalidOperationException("Simulated assertion failure");
        }));
        Assert.NotNull(directory);
        Assert.False(Directory.Exists(directory));
    });
}
