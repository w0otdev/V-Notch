using System.IO;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class FileIconProviderBehaviorTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplicationIconIsFrozenAndReusedUntilCacheClears(bool small) => SharedStaTestRunner.Run(() =>
    {
        string executable = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var icon = FileIconProvider.GetAppIcon(executable, small);
        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen);
        Assert.Same(icon, FileIconProvider.GetAppIcon(executable, small));
        FileIconProvider.ClearCache();
        Assert.NotSame(icon, FileIconProvider.GetAppIcon(executable, small));
    });

    [Fact]
    public void FileThumbnailsAreFrozenCachedAndInvalidatable() => SharedStaTestRunner.Run(() =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-icon-").FullName;
        try
        {
            string image = Path.Combine(root, "image.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "white-mat-artwork.png"), image);
            var first = Assert.IsAssignableFrom<BitmapSource>(FileIconProvider.GetFileIcon(image));
            Assert.True(first.IsFrozen);
            Assert.True(first.PixelWidth > 0);
            Assert.Same(first, FileIconProvider.GetFileIcon(image));
            FileIconProvider.Invalidate(image);
            Assert.NotSame(first, FileIconProvider.GetFileIcon(image));
            Assert.Null(FileIconProvider.GetAppIcon(Path.Combine(root, "missing.exe")));
            Assert.Null(FileIconProvider.GetAppIcon(""));
        }
        finally { FileIconProvider.ClearCache(); Directory.Delete(root, true); }
    });

    [Theory]
    [InlineData("<Icon Index=\"0\" File=\"%SystemRoot%\\system32\\mmc.exe\" />")]
    [InlineData("<Console />")]
    [InlineData("<Icon Index=\"99999999999999999999\" File=\"missing.dll\" />")]
    public void ConsoleDocumentsFallBackToUsableShellIcons(string xml) => SharedStaTestRunner.Run(() =>
    {
        string root = Directory.CreateTempSubdirectory("vnotch-msc-icon-").FullName;
        try
        {
            string file = Path.Combine(root, "fixture.msc");
            File.WriteAllText(file, xml);
            foreach (bool small in new[] { true, false })
            {
                var icon = FileIconProvider.GetAppIcon(file, small);
                Assert.NotNull(icon);
                Assert.True(icon.IsFrozen);
            }
        }
        finally { FileIconProvider.ClearCache(); Directory.Delete(root, true); }
    });
}
