using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class YouTubeQuotaCooldownTests
{
    [Fact]
    public void ReplacementKeyWorksEvenWhenAnOldRequestReportsQuotaLate()
    {
        var cooldown = new YouTubeQuotaCooldown();
        cooldown.Trip("old-key");
        Assert.True(cooldown.IsActive("old-key"));
        Assert.False(cooldown.IsActive("replacement-key"));
        cooldown.Trip("old-key");
        Assert.False(cooldown.IsActive("replacement-key"));
        cooldown.Trip("replacement-key");
        Assert.True(cooldown.IsActive("old-key"));
        Assert.True(cooldown.IsActive("replacement-key"));
    }

    [Fact]
    public void CooldownExpiresAndWhitespaceDoesNotBypassIt()
    {
        DateTime now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var cooldown = new YouTubeQuotaCooldown(() => now);
        cooldown.Trip(" key ");
        Assert.True(cooldown.IsActive("key"));
        now = new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);
        Assert.False(cooldown.IsActive("key"));
    }
}
