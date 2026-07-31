using VNotch.Models;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightRankerTests
{
    [Fact]
    public void Score_IsCaseAndAccentInsensitive()
    {
        var fileItem = new SpotlightSearchItem("id", SpotlightResultKind.File, "Ứng Dụng.txt", "Documents", "C:\\Ứng Dụng.txt");
        var appItem = Item("Ứng Dụng");

        Assert.Equal(1000, SpotlightRanker.Score(fileItem, "ung dung"));
        Assert.True(SpotlightRanker.Score(appItem, "ung dung") > 1000);
    }

    [Fact]
    public void Score_PrioritizesExactPrefixAndFuzzyMatches()
    {
        var exact = SpotlightRanker.Score(Item("Calculator"), "calculator");
        var prefix = SpotlightRanker.Score(Item("Calculator Plus"), "calc");
        var fuzzy = SpotlightRanker.Score(Item("Calculator"), "calcualtor");

        Assert.True(exact > prefix);
        Assert.True(prefix > fuzzy);
        Assert.True(fuzzy > 0);
    }

    [Fact]
    public void Score_CanMatchTheSubtitle()
    {
        var item = Item("Report", @"C:\Users\me\Documents");

        Assert.True(SpotlightRanker.Score(item, "documents") > 0);
    }

    [Fact]
    public void Score_PrioritizesPopularExecutablesOverOtherApps()
    {
        var cmdExe = Item("cmd.exe", "C:\\Windows\\system32", "C:\\Windows\\system32\\cmd.exe");
        var cmDust = Item("CmDust", "Application", "C:\\Program Files\\CmDust.exe");

        var scoreCmdExe = SpotlightRanker.Score(cmdExe, "cmd");
        var scoreCmDust = SpotlightRanker.Score(cmDust, "cmd");

        Assert.True(scoreCmdExe > scoreCmDust, $"Expected cmd.exe ({scoreCmdExe}) to rank higher than CmDust ({scoreCmDust}) for query 'cmd'");
    }

    [Fact]
    public void Score_MatchesExecutableTitleWithoutExtensionAsExactMatch()
    {
        var cmdExe = Item("cmd.exe", "C:\\Windows\\system32", "C:\\Windows\\system32\\cmd.exe");
        var score = SpotlightRanker.Score(cmdExe, "cmd");

        Assert.True(score >= 1050, $"Expected score >= 1050 for cmd.exe on query 'cmd', got {score}");
    }

    [Fact]
    public void Score_PrioritizesExecutablesAndAppsOverJunkAssetFiles()
    {
        var chromeApp = Item("Google Chrome", "Application", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe");
        var chromeDds = new SpotlightSearchItem("file:Chrome.dds", SpotlightResultKind.File, "Chrome.dds", "C:\\Textures", "C:\\Textures\\Chrome.dds");

        var appScore = SpotlightRanker.Score(chromeApp, "chrome");
        var ddsScore = SpotlightRanker.Score(chromeDds, "chrome");

        Assert.True(appScore > ddsScore + 300, $"Expected Google Chrome ({appScore}) to rank far higher than Chrome.dds ({ddsScore})");
    }

    private static SpotlightSearchItem Item(string title, string subtitle = "Application", string target = "target") =>
        new("id", SpotlightResultKind.Application, title, subtitle, target);
}
