using System;
using System.IO;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SetupIntegrityStartupTests
{
    [Theory]
    [InlineData(new[] { "--setup" }, @"C:\App\V-Notch.exe", true)]
    [InlineData(new[] { "--update" }, @"C:\App\V-Notch.exe", true)]
    [InlineData(new[] { "--uninstall" }, @"C:\App\V-Notch.exe", true)]
    [InlineData(new[] { "--setup-source", @"C:\Payload" }, @"C:\App\V-Notch.exe", true)]
    [InlineData(new string[0], @"C:\Temp\V-Notch-Setup.exe", true)]
    [InlineData(new string[0], @"C:\App\V-Notch.exe", false)]
    public void IsSetupOrUninstall_DetectsInstallerAndUpdateEntryPoints(string[] args, string processPath, bool expected)
    {
        Assert.Equal(expected, App.IsSetupOrUninstall(args, processPath));
    }

    [Fact]
    public void SetupWindow_AutoUpdateWithValidInstallPath_SkipsInteractivePagesAndStartsInstall()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vnotch-autoupdate-{Guid.NewGuid():N}")).FullName;
        string sourceDir = Directory.CreateDirectory(Path.Combine(root, "payload")).FullName;
        string installDir = Directory.CreateDirectory(Path.Combine(root, "installed")).FullName;
        File.WriteAllText(Path.Combine(sourceDir, "V-Notch.exe"), "new binary");
        File.WriteAllText(Path.Combine(installDir, "V-Notch.exe"), "existing binary");

        try
        {
            SetupInstallOptions? capturedOptions = null;
            var pendingInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            SharedStaTestRunner.Run(() =>
            {
                LocalizedPresentation.Initialize();
                var autoWindow = new SetupWindow(
                    sourceDir,
                    "vi",
                    (opts, _) =>
                    {
                        capturedOptions = opts;
                        return pendingInstall.Task;
                    },
                    installDir,
                    isAutoUpdate: true);
                try
                {
                    autoWindow.InitializeForTest();
                    Assert.Equal(6, autoWindow.CurrentPageIndex);
                    Assert.NotNull(capturedOptions);
                    Assert.Equal(installDir, capturedOptions!.InstallDirectory);
                    Assert.Equal("vi", capturedOptions.Language);
                }
                finally
                {
                    pendingInstall.TrySetResult();
                    autoWindow.Close();
                }

                bool manualTriggered = false;
                var interactiveWindow = new SetupWindow(
                    sourceDir,
                    "en",
                    (_, _) =>
                    {
                        manualTriggered = true;
                        return Task.CompletedTask;
                    },
                    installDir,
                    isAutoUpdate: false);
                try
                {
                    interactiveWindow.InitializeForTest();
                    Assert.Equal(2, interactiveWindow.CurrentPageIndex);
                    Assert.False(manualTriggered);
                }
                finally
                {
                    interactiveWindow.Close();
                }
            });
        }
        finally
        {
            SafeFileDeletion.DeleteDirectory(root);
        }
    }
}
