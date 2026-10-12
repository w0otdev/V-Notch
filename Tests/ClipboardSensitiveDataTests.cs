using System.IO;
using VNotch.Models;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardSensitiveDataTests
{
    [Theory]
    [MemberData(nameof(SyntheticProviderTokens))]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.signature")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nexample")]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("password: correct-horse")]
    [InlineData("{\"api_key\":\"example-secret\"}")]
    [InlineData("123456")]
    [InlineData("mật khẩu: abc123XYZ!")]
    [InlineData("mat khau: Abc@123")]
    [InlineData("mật khẩu là 123456")]
    [InlineData("mk: secretPass#1")]
    [InlineData("pass: secret123")]
    [InlineData("passcode: 987654")]
    [InlineData("pin: 4321")]
    [InlineData("mã pin: 8888")]
    [InlineData("db_password=mySuperSecret")]
    [InlineData("client_secret: \"secret-value\"")]
    [InlineData("wifi password: MyHomeWifi123")]
    [InlineData("Mã xác thực của bạn là 849201")]
    [InlineData("Mã OTP: 394812")]
    [InlineData("Your verification code is: 593021")]
    [InlineData("G-849201")]
    [InlineData("123-456")]
    [InlineData("postgres://user:password123@localhost:5432/db")]
    [InlineData("mysql://root:secret@127.0.0.1:3306/prod")]
    [InlineData("redis://:mypassword@cache.local:6379")]
    [InlineData("Server=myServer;Database=db;Uid=usr;Pwd=P@ssw0rd123;")]
    [InlineData("WIFI:T:WPA;S:Home;P:SecretPassword123;;")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.signature")]
    [InlineData("P@ssw0rd123!")]
    [InlineData("Admin@2024")]
    [InlineData("kP9#vL2!xQ@1")]
    [InlineData("vX9$mL#2pQ!")]
    [InlineData("admin#123456")]
    [InlineData("correct-horse-battery-staple")]
    [InlineData("8xK9mP2aQ0zL")]
    public void RecognizesSecrets(string text) => Assert.True(ClipboardSensitiveData.IsSensitive(text));

    // Deliberately synthetic. Build token-shaped inputs at runtime instead of
    // committing complete credential literals that trigger push protection.
    public static IEnumerable<object[]> SyntheticProviderTokens()
    {
        yield return new object[] { string.Concat("sk-proj-", new string('a', 32)) };
        yield return new object[] { string.Concat("AIza", new string('a', 35)) };
        yield return new object[] { string.Concat("sk-ant-", new string('a', 42)) };
        yield return new object[] { $"xoxb-{new string('1', 12)}-{new string('2', 12)}-{new string('a', 26)}" };
        yield return new object[] { string.Concat("sk_", "live_", new string('a', 24)) };
    }

    [Theory]
    [InlineData("ordinary notes for tomorrow")]
    [InlineData("4111 1111 1111 1112")]
    [InlineData("0000000000000000")]
    [InlineData("https://example.com")]
    [InlineData("https://github.com/rainaku/V-Notch")]
    [InlineData("ClipboardSensitiveDataTests")]
    [InlineData("OnPropertyChanged")]
    [InlineData("bufferSize1024")]
    [InlineData("66ff0f75-4ccd-4414-bb16-65a8bd714442")]
    [InlineData("e60ccc4")]
    [InlineData("#FF0000")]
    [InlineData("C:\\Users\\gauze\\Documents\\project.cs")]
    [InlineData("int count = list.Count;")]
    [InlineData("Xin chào bạn, hôm nay thế nào?")]
    public void LeavesOrdinaryTextVisible(string text) => Assert.False(ClipboardSensitiveData.IsSensitive(text));

    [Theory]
    [InlineData("any copied text", "Bitwarden", "Bitwarden.exe")]
    [InlineData("any copied text", "1Password", "1Password.exe")]
    [InlineData("any copied text", "KeePassXC", "KeePassXC.exe")]
    public void RecognizesPasswordManagerSource(string text, string app, string exe) =>
        Assert.True(ClipboardSensitiveData.IsSensitive(text, app, exe));

    [Fact]
    public void MasksCardTitleAndPreview() => SharedStaTestRunner.Run(() =>
    {
        var card = new ClipboardCardViewModel(new ClipboardEntry { IsSensitive = true, Title = "password: secret", Preview = "password: secret" });
        Assert.Equal(ClipboardSensitiveData.Mask, card.Title);
        Assert.Equal(ClipboardSensitiveData.Mask, card.Preview);
    });

    [Fact]
    public async Task ExpiryRespectsOptionBoundaryAndRecopyAndRemovesDiskContent()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            var now = DateTime.UtcNow;
            var capture = new ClipboardCapture { Text = "password: secret", CopiedUtc = now };
            var entry = (await store.ImportAsync(capture)).Entry!;
            Assert.True(entry.IsSensitive);
            Assert.Equal(capture.Text, (await store.GetContentAsync(entry.Id)).Text);
            await store.UpdateAsync(entry.Id, pinned: true, archived: true);
            await store.ExpireSensitiveAsync(now.AddSeconds(61));
            Assert.NotNull(store.GetEntry(entry.Id));
            store.AutoDeleteSensitive = true;
            await store.ExpireSensitiveAsync(now.AddSeconds(59));
            Assert.NotNull(store.GetEntry(entry.Id));
            await store.ImportAsync(capture with { CopiedUtc = now.AddSeconds(30) });
            await store.ExpireSensitiveAsync(now.AddSeconds(60));
            Assert.NotNull(store.GetEntry(entry.Id));
            await store.ExpireSensitiveAsync(now.AddSeconds(90));
            Assert.Null(store.GetEntry(entry.Id));
            Assert.False(Directory.Exists(Path.Combine(root, "items", entry.Id.ToString("N"))));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LockedPersonalExpiresAfterRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var pin = new System.Security.SecureString();
        foreach (char c in "123456") pin.AppendChar(c);
        var now = DateTime.UtcNow;
        Guid id;
        try
        {
            await using (var store = new ClipboardHistoryStore(root))
            {
                await store.InitializeAsync();
                Assert.True(await store.UnlockPersonalAsync(pin, create: true));
                id = (await store.ImportAsync(new ClipboardCapture { Text = "password: secret", TargetCategory = "Personal", CopiedUtc = now })).Entry!.Id;
                await store.LockPersonalAsync();
            }
            await using var reopened = new ClipboardHistoryStore(root) { AutoDeleteSensitive = true };
            await reopened.InitializeAsync();
            await reopened.ExpireSensitiveAsync(now.AddSeconds(60));
            Assert.False(Directory.Exists(Path.Combine(root, "personal", id.ToString("N"))));
            Assert.True(await reopened.UnlockPersonalAsync(pin));
            Assert.Empty(reopened.Snapshot(personal: true));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ScansBeyondPreviewAndRichTextAndMigratesOldHistory()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Guid id;
            await using (var store = new ClipboardHistoryStore(root))
            {
                await store.InitializeAsync();
                var entry = (await store.ImportAsync(new ClipboardCapture { Text = new string('x', 2000) + " password: secret" })).Entry!;
                id = entry.Id;
                Assert.True(entry.IsSensitive);
                Assert.Equal(ClipboardSensitiveData.Mask, entry.Title);
                Assert.Equal(ClipboardSensitiveData.Mask, entry.Preview);
                var rich = (await store.ImportAsync(new ClipboardCapture { Text = "rich text", Html = "<span>password: secret</span>" })).Entry!;
                Assert.True(rich.IsSensitive);
            }
            string metadata = Path.Combine(root, "items", id.ToString("N"), "entry.json");
            var old = System.Text.Json.JsonSerializer.Deserialize<ClipboardEntry>(await File.ReadAllTextAsync(metadata))!;
            await File.WriteAllTextAsync(metadata, System.Text.Json.JsonSerializer.Serialize(old with { IsSensitive = false, Title = "old title", Preview = "old preview" }));
            await using var reopened = new ClipboardHistoryStore(root);
            await reopened.InitializeAsync();
            Assert.True(reopened.GetEntry(id)!.IsSensitive);
            Assert.Equal(ClipboardSensitiveData.Mask, reopened.GetEntry(id)!.Preview);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MovingToPersonalKeepsExpiryAndOrdinaryItemsSurvive()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var pin = new System.Security.SecureString();
        foreach (char c in "123456") pin.AppendChar(c);
        try
        {
            await using var store = new ClipboardHistoryStore(root) { AutoDeleteSensitive = true };
            await store.InitializeAsync();
            var now = DateTime.UtcNow;
            var secret = (await store.ImportAsync(new ClipboardCapture { Text = "password: secret", CopiedUtc = now })).Entry!;
            var ordinary = (await store.ImportAsync(new ClipboardCapture { Text = "ordinary note", CopiedUtc = now })).Entry!;
            Assert.True(await store.UnlockPersonalAsync(pin, create: true));
            await store.MovePersonalAsync(secret.Id, true);
            await store.LockPersonalAsync();
            await store.ExpireSensitiveAsync(now.AddSeconds(60));
            Assert.NotNull(store.GetEntry(ordinary.Id));
            Assert.False(Directory.Exists(Path.Combine(root, "personal", secret.Id.ToString("N"))));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
