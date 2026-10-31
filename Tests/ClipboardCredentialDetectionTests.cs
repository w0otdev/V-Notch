using System.IO;
using System.Text;
using System.Text.Json;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardCredentialDetectionTests
{
    [Theory]
    [InlineData("Example2005@")]
    [InlineData("MậtKhẩu2026!")]
    [InlineData("秘密2026!秘密")]
    [InlineData("password: a")]
    [InlineData("password: password")]
    [InlineData("password: changeme")]
    [InlineData("passphrase = \"four ordinary words here\"")]
    [InlineData("export CLIENT_SECRET='synthetic-value'")]
    [InlineData("{\"refresh_token\": \"synthetic-value\"}")]
    [InlineData("password: report.pdf")]
    [InlineData("password: aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwx")]
    [InlineData("postgresql://user:p@database.local/db")]
    [InlineData("Your security code is 1234")]
    [InlineData("Mã xác nhận của bạn là 678901")]
    [InlineData("aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")]
    [InlineData("user_password = supersecretvalue")]
    [InlineData("admin_password: supersecretvalue")]
    [InlineData("jwt_secret=supersecretvalue")]
    [InlineData("app_secret=supersecretvalue")]
    [InlineData("private_key=supersecretvalue")]
    public void MasksCredentialEvidence(string text) => Assert.True(ClipboardSensitiveData.Analyze(text).ShouldMask);

    [Fact]
    public void RecognizesSnakeCasePrefixedAndSuffixedCredentialLabels()
    {
        Assert.True(ClipboardSensitiveData.IsSensitive("aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "", ""));
        Assert.True(ClipboardSensitiveData.IsSensitive("user_password = supersecretvalue", "", ""));
        Assert.True(ClipboardSensitiveData.IsSensitive("admin_password: supersecretvalue", "", ""));
        Assert.True(ClipboardSensitiveData.IsSensitive("jwt_secret=supersecretvalue", "", ""));
        Assert.True(ClipboardSensitiveData.IsSensitive("app_secret=supersecretvalue", "", ""));
        Assert.True(ClipboardSensitiveData.IsSensitive("private_key=supersecretvalue", "", ""));
    }

    [Theory]
    [InlineData("john.smith+work@example.com")]
    [InlineData("Release2026@company.example")]
    [InlineData("E:\\Projects\\Release2026!\\main.ts")]
    [InlineData("Z:/Projects/Build2026!/index.ts")]
    [InlineData("/home/user/Release2026!")]
    [InlineData("../Build2026!/index.ts")]
    [InlineData("Report2026!.pdf")]
    [InlineData("v1.2.3-beta4")]
    [InlineData("Console.WriteLine(123)")]
    [InlineData("count!=OtherCount2026")]
    [InlineData("SHA256HashAlgorithm")]
    [InlineData("Http2RequestHandler")]
    [InlineData("password-reset-guide-for-users")]
    [InlineData("build-release-notes-for-project")]
    [InlineData("password is required")]
    [InlineData("password: null")]
    [InlineData("password: ********")]
    [InlineData("password=${DB_PASSWORD}")]
    [InlineData("password=process.env.DB_PASSWORD")]
    [InlineData("access_token_count=123456")]
    [InlineData("2026")]
    [InlineData("192.168.1.1")]
    [InlineData("#AABBCCDD")]
    public void DoesNotMaskOrdinaryClipboardContent(string text) => Assert.False(ClipboardSensitiveData.Analyze(text).ShouldMask);

    [Theory]
    [InlineData("Bitwarden", "Bitwarden.exe", true)]
    [InlineData("Bitwarden", "C:\\Apps\\Bitwarden.exe", true)]
    [InlineData("Bitwarden", "chrome.exe", false)]
    [InlineData("Bitwarden documentation", "", false)]
    [InlineData("not-bitwarden", "not-bitwarden.exe", false)]
    [InlineData("KeePassXC", "", true)]
    public void SourceIdentityUsesWholeNames(string app, string executable, bool expected) =>
        Assert.Equal(expected, ClipboardSensitiveData.IsSensitive("ordinary text", app, executable));

    [Fact]
    public void NormalizesUnicodeLabelsAndDoesNotLimitPasswordManagerCopies()
    {
        Assert.True(ClipboardSensitiveData.IsSensitive("mật khẩu: a".Normalize(NormalizationForm.FormD)));
        Assert.Equal(ClipboardSensitivityReason.PasswordManager,
            ClipboardSensitiveData.Analyze(new string('x', 4096), "Bitwarden", "Bitwarden.exe").Reason);
    }

    [Fact]
    public void AmbiguousStringsAreDistinguishedFromStrongEvidence()
    {
        var ambiguous = ClipboardSensitiveData.Analyze("correct-horse-battery-staple");
        Assert.Equal(ClipboardSensitivityConfidence.Possible, ambiguous.Confidence);
        Assert.False(ambiguous.ShouldMask);
        Assert.Equal(ClipboardSensitivityConfidence.High,
            ClipboardSensitiveData.Analyze("passphrase: correct-horse-battery-staple").Confidence);
        Assert.Equal(ClipboardSensitivityConfidence.Likely, ClipboardSensitiveData.Analyze("Example2005@").Confidence);
    }

    [Fact]
    public void InspectsBeyondPreviewAndFailsClosedAtResourceLimit()
    {
        Assert.Equal(ClipboardSensitivityReason.CredentialLabel,
            ClipboardSensitiveData.Analyze(new string('x', 20_000) + "\npassword: a").Reason);
        var oversized = ClipboardSensitiveData.Analyze(new string('x', 1_000_001));
        Assert.True(oversized.ShouldMask);
        Assert.Equal(ClipboardSensitivityReason.InspectionLimit, oversized.Reason);
    }

    [Fact]
    public void JwtRequiresJsonStructureAndBase64Segments()
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string header = Encode("{\"alg\":\"HS256\"}");
        string payload = Encode("{\"sub\":\"synthetic-user\"}");
        string signature = Encode("synthetic-signature");
        Assert.Equal(ClipboardSensitivityReason.CredentialFormat,
            ClipboardSensitiveData.Analyze($"{header}.{payload}.{signature}").Reason);
        Assert.False(ClipboardSensitiveData.Analyze($"{header}.{Encode("not JSON")}.{signature}").ShouldMask);
    }

    [Fact]
    public async Task NewDetectorRechecksOldNegativeMetadataAndPersistsVersion()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Guid id;
            await using (var store = new ClipboardHistoryStore(root))
            {
                await store.InitializeAsync();
                id = (await store.ImportAsync(new ClipboardCapture { Text = "password: a" })).Entry!.Id;
            }
            string path = Path.Combine(root, "items", id.ToString("N"), "entry.json");
            var entry = JsonSerializer.Deserialize<ClipboardEntry>(await File.ReadAllTextAsync(path))!;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry with
            {
                IsSensitive = false,
                SensitivityVersion = ClipboardSensitiveData.DetectorVersion - 1,
                Title = "password: a",
                Preview = "password: a"
            }));
            await using var reopened = new ClipboardHistoryStore(root);
            await reopened.InitializeAsync();
            Assert.True(reopened.GetEntry(id)!.IsSensitive);
            Assert.Empty(await reopened.SearchAsync("password: a", "All"));
            var persisted = JsonSerializer.Deserialize<ClipboardEntry>(await File.ReadAllTextAsync(path))!;
            Assert.Equal(ClipboardSensitiveData.DetectorVersion, persisted.SensitivityVersion);
            Assert.Equal(ClipboardSensitiveData.Mask, persisted.Title);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
