using VNotch.Models;
using VNotch.Services.AiUsage;
using Xunit;

namespace VNotch.Tests;

public sealed class AiUsagePollPolicyTests
{
    [Fact]
    public void UnavailableProviderBacksOffAndSuccessfulReadRestoresFreshness()
    {
        var policy = new AiUsagePollPolicy();
        Assert.Equal(TimeSpan.FromMinutes(4), policy.After(AiUsageState.Unavailable));
        Assert.Equal(TimeSpan.FromMinutes(8), policy.After(AiUsageState.Unavailable));
        for (int i = 0; i < 8; i++) Assert.Equal(TimeSpan.FromMinutes(10), policy.After(AiUsageState.Unavailable));
        Assert.Equal(TimeSpan.FromMinutes(2), policy.After(AiUsageState.Ready));
        Assert.Equal(TimeSpan.FromMinutes(4), policy.After(AiUsageState.Unavailable));
        policy.Reset();
        Assert.Equal(TimeSpan.FromMinutes(4), policy.After(AiUsageState.Unavailable));
    }

    [Theory]
    [InlineData((int)AiUsageState.NotInstalled, 10)]
    [InlineData((int)AiUsageState.SignedOut, 5)]
    [InlineData((int)AiUsageState.Unsupported, 5)]
    public void InactiveAccountsDoNotStartAChildEveryTwoMinutes(int state, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), new AiUsagePollPolicy().After((AiUsageState)state));
    }
}
