using VNotch.Controls;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardTrayDropPolicyTests
{
    [Theory]
    [InlineData("Images", "photo.png", true)]
    [InlineData("Files", "archive.zip", true)]
    [InlineData("Images", "archive.zip", false)]
    [InlineData("Files", "photo.png", false)]
    [InlineData("Text", "archive.zip", false)]
    [InlineData("All", "archive.zip", true)]
    [InlineData("Receipts", "archive.zip", true)]
    public void ExternalDropsRespectDetectedKind(string category, string path, bool expected)
        => Assert.Equal(expected, ClipboardTray.CanDropCategory(category, [], [path]));

    [Theory]
    [InlineData(@"\\attacker\share\secret.txt")]
    [InlineData("//attacker/share/secret.txt")]
    [InlineData("file://attacker/share/secret.txt")]
    [InlineData(@"\\?\UNC\attacker\share\secret.txt")]
    public void EveryCategoryRejectsNetworkFiles(string path)
    {
        foreach (string category in new[] { "All", "Files", "Personal", "Pin", "Archive", "Work" })
            Assert.False(ClipboardTray.CanDropCategory(category, [], [path]));
    }

    [Theory]
    [InlineData("Text", ClipboardKind.Text)]
    [InlineData("Code", ClipboardKind.Code)]
    [InlineData("Links", ClipboardKind.Link)]
    [InlineData("Colors", ClipboardKind.Color)]
    [InlineData("Images", ClipboardKind.Image)]
    [InlineData("Files", ClipboardKind.File)]
    public void ExistingEntriesMustAllMatchTheTypeTab(string category, ClipboardKind kind)
    {
        Assert.True(ClipboardTray.CanDropCategory(category, [new() { Kind = kind }], []));
        var other = kind == ClipboardKind.Text ? ClipboardKind.File : ClipboardKind.Text;
        Assert.False(ClipboardTray.CanDropCategory(category, [new() { Kind = kind }, new() { Kind = other }], []));
        Assert.False(ClipboardTray.CanDropCategory(category, [], []));
    }
}
