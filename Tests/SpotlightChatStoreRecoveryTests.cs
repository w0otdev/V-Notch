using System.IO;
using VNotch.Services;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SpotlightChatStoreRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vnotch-chat-recovery-" + Guid.NewGuid().ToString("N"));
    private string ChatPath => Path.Combine(_directory, "chats.enc");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptHistoryIsPreservedAndNewChatsCanBeSaved(bool validEncryption)
    {
        Directory.CreateDirectory(_directory);
        string original = validEncryption ? DataProtection.Protect("{invalid JSON") : "enc:invalid base64";
        File.WriteAllText(ChatPath, original);
        var store = new SpotlightChatStore(ChatPath);

        Assert.Empty(store.Load());
        Assert.False(File.Exists(ChatPath));
        string recovery = Assert.Single(Directory.GetFiles(_directory, "chats.enc.*.corrupt"));
        Assert.Equal(original, File.ReadAllText(recovery));
        Assert.True(store.Save([new SpotlightSavedChat { Draft = "new conversation" }]));
        Assert.Equal("new conversation", Assert.Single(new SpotlightChatStore(ChatPath).Load()).Draft);
        Assert.Equal(original, File.ReadAllText(recovery));
    }

    [Fact]
    public void FailedQuarantinePreservesTheEncryptedOriginalUntilARetrySucceeds()
    {
        Directory.CreateDirectory(_directory);
        const string original = "enc:invalid base64";
        File.WriteAllText(ChatPath, original);
        var store = new SpotlightChatStore(ChatPath);
        using (var handle = new FileStream(ChatPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Empty(store.Load());
            Assert.False(store.Save([new SpotlightSavedChat { Draft = "must not overwrite" }]));
            Assert.Equal(original, File.ReadAllText(ChatPath));
            Assert.Empty(Directory.GetFiles(_directory, "*.corrupt"));
        }
        Assert.Empty(store.Load());
        Assert.Equal(original, File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.corrupt"))));
        Assert.True(store.Save([new SpotlightSavedChat { Draft = "new history" }]));
    }

    [Fact]
    public void TemporarilyLockedHistoryIsNotQuarantinedOrOverwritten()
    {
        var store = new SpotlightChatStore(ChatPath);
        Assert.True(store.Save([new SpotlightSavedChat { Draft = "existing conversation" }]));
        string original = File.ReadAllText(ChatPath);
        using (var handle = new FileStream(ChatPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(store.Load());
            Assert.False(store.Save([new SpotlightSavedChat { Draft = "must not overwrite" }]));
            Assert.Empty(Directory.GetFiles(_directory, "*.corrupt"));
        }
        Assert.Equal(original, File.ReadAllText(ChatPath));
        Assert.Equal("existing conversation", Assert.Single(store.Load()).Draft);
        Assert.True(store.Save([new SpotlightSavedChat { Draft = "after retry" }]));
        Assert.Equal("after retry", Assert.Single(store.Load()).Draft);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
