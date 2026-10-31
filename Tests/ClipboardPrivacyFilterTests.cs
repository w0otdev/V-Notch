using System.IO;
using System.Windows;
using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardPrivacyFilterTests
{
    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("Clipboard Viewer Ignore")]
    public void PrivacyMarkerOverridesExplicitHistoryOptIn(string format) => SharedStaTestRunner.Run(() =>
    {
        var data = new DataObject();
        data.SetText("sensitive text");
        data.SetData(format, new MemoryStream([0, 0, 0, 0]), false);
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream([1, 0, 0, 0]), false);
        Assert.True(ClipboardHistoryController.ShouldIgnoreClipboardData(data));
    });

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(256, false)]
    public void SerializedDwordIsReadWithoutChangingStreamPosition(int flag, bool ignored) => SharedStaTestRunner.Run(() =>
    {
        byte[] bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, flag);
        using var stream = new MemoryStream(bytes);
        stream.Position = 2;
        var data = new DataObject();
        data.SetData("CanIncludeInClipboardHistory", stream, false);
        Assert.Equal(ignored, ClipboardHistoryController.ShouldIgnoreClipboardData(data));
        Assert.Equal(2, stream.Position);
        Assert.True(stream.CanRead);
    });

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void InProcessDwordRepresentationsAreSupported(int flag, bool ignored) => SharedStaTestRunner.Run(() =>
    {
        byte[] bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, flag);
        foreach (object value in new object[] { flag, (uint)flag, bytes })
        {
            var data = new DataObject();
            data.SetData("CanIncludeInClipboardHistory", value, false);
            Assert.Equal(ignored, ClipboardHistoryController.ShouldIgnoreClipboardData(data));
        }
    });

    [Fact]
    public void OrdinaryClipboardDataRemainsEligible() => SharedStaTestRunner.Run(() =>
    {
        var data = new DataObject();
        data.SetText("ordinary text");
        Assert.False(ClipboardHistoryController.ShouldIgnoreClipboardData(data));
    });

    [Fact]
    public async Task SensitiveExpiryMarkersAreDeletedOnRemoveAndNeverCreatedForPersonalItems()
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-privacy-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new VNotch.Services.Clipboard.ClipboardHistoryStore(root);
            await store.InitializeAsync();

            string expiryDir = Path.Combine(root, "sensitive-expiry");
            var publicSensitive = (await store.ImportAsync(new VNotch.Models.ClipboardCapture
            {
                Text = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.publictoken"
            })).Entry!;
            Assert.True(publicSensitive.IsSensitive);
            Assert.True(File.Exists(Path.Combine(expiryDir, publicSensitive.Id.ToString("N"))));

            await store.DeleteAsync(publicSensitive.Id);
            Assert.False(File.Exists(Path.Combine(expiryDir, publicSensitive.Id.ToString("N"))));

            using var pin = new System.Security.SecureString();
            foreach (char c in "123456") pin.AppendChar(c);
            pin.MakeReadOnly();
            Assert.True(await store.UnlockPersonalAsync(pin, create: true));

            var personalSensitive = (await store.ImportAsync(new VNotch.Models.ClipboardCapture
            {
                Text = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.personaltoken",
                TargetCategory = "Personal"
            })).Entry!;
            Assert.True(personalSensitive.IsPersonal);
            Assert.True(personalSensitive.IsSensitive);
            Assert.False(File.Exists(Path.Combine(expiryDir, personalSensitive.Id.ToString("N"))));

            await store.ResetPersonalAsync();
            Assert.False(Directory.Exists(expiryDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MalformedHistoryMarkersAreIgnored() => SharedStaTestRunner.Run(() =>
    {
        foreach (object value in new object[] { new byte[3], new MemoryStream([0, 0]), "invalid" })
        {
            var data = new DataObject();
            data.SetData("CanIncludeInClipboardHistory", value, false);
            Assert.True(ClipboardHistoryController.ShouldIgnoreClipboardData(data));
            (value as IDisposable)?.Dispose();
        }
    });
}
