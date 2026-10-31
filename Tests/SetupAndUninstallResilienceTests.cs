using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Xunit;

namespace VNotch.Tests;

public class SetupAndUninstallResilienceTests
{
    [Fact]
    public void CancelDismissal_AfterInstallFailure_DoesNotRetriggerInstall_AndRestoresFailureButtons()
    {
        RunOnStaThread(() =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "VNotchSetupTest_" + Guid.NewGuid().ToString("N"));
            string sourceDir = Path.Combine(tempRoot, "source");
            string installDir = Path.Combine(tempRoot, "install");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);

            try
            {
                int installAttempts = 0;
                var window = new SetupWindow(
                    sourceDir,
                    "en",
                    (_, _) =>
                    {
                        installAttempts++;
                        return Task.FromException(new IOException("Simulated file lock"));
                    },
                    installDir);

                window.InitializeForTest();
                window.NavigateToPage(6);

                Assert.Equal(1, installAttempts);
                Assert.True(window.HasInstallFailed);
                Assert.Equal(6, window.CurrentPageIndex);
                Assert.Equal(Visibility.Visible, window.BackButton.Visibility);
                Assert.Equal(Visibility.Visible, window.CancelButton.Visibility);
                Assert.Equal(Visibility.Collapsed, window.NextButton.Visibility);
                Assert.False(window.InstallProgressStepPage.IsProgressIndeterminate);
                Assert.Contains("Simulated file lock", window.InstallProgressStepPage.StatusText, StringComparison.Ordinal);

                // Click Cancel to open the confirmation page, then click Keep Setup (BackButton) to dismiss it.
                window.TriggerCancel();
                Assert.True(window.IsShowingCancelSetupPage);

                window.TriggerBack();
                Assert.False(window.IsShowingCancelSetupPage);
                Assert.Equal(6, window.CurrentPageIndex);
                Assert.Equal(1, installAttempts);
                Assert.True(window.HasInstallFailed);
                Assert.False(window.InstallProgressStepPage.IsProgressIndeterminate);
                Assert.Contains("Simulated file lock", window.InstallProgressStepPage.StatusText, StringComparison.Ordinal);
                Assert.Equal(Visibility.Visible, window.BackButton.Visibility);
                Assert.Equal(Visibility.Visible, window.CancelButton.Visibility);
                Assert.Equal(Visibility.Collapsed, window.NextButton.Visibility);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        });
    }

    [Fact]
    public void FailedUpgrade_AllowsNavigatingBackToDirectoryPage_AndRetryingInstallInNewDirectory()
    {
        RunOnStaThread(() =>
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "VNotchUpgradeTest_" + Guid.NewGuid().ToString("N"));
            string sourceDir = Path.Combine(tempRoot, "source");
            string lockedUpgradeDir = Path.Combine(tempRoot, "existing");
            string newInstallDir = Path.Combine(tempRoot, "alternate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(lockedUpgradeDir);
            Directory.CreateDirectory(newInstallDir);
            File.WriteAllBytes(Path.Combine(lockedUpgradeDir, "V-Notch.exe"), [0x4D, 0x5A]);

            try
            {
                int installAttempts = 0;
                SetupInstallOptions? lastOptions = null;

                var window = new SetupWindow(
                    sourceDir,
                    "en",
                    (options, _) =>
                    {
                        installAttempts++;
                        lastOptions = options;
                        if (installAttempts == 1)
                        {
                            return Task.FromException(new UnauthorizedAccessException("Upgrade folder locked"));
                        }

                        return Task.CompletedTask;
                    },
                    lockedUpgradeDir);

                window.InitializeForTest();
                Assert.Equal(2, window.CurrentPageIndex);

                window.NavigateToPage(6);
                Assert.Equal(1, installAttempts);
                Assert.True(window.HasInstallFailed);
                Assert.Equal(6, window.CurrentPageIndex);

                // Clicking Back after a failed upgrade must navigate to DirectoryPage (index 4).
                window.TriggerBack();
                Assert.Equal(4, window.CurrentPageIndex);

                window.DirectoryStepPage.InstallPath = newInstallDir;

                // Clicking Next from DirectoryPage on an upgrade must start a fresh install attempt on page 6 (and advance to 7 on success).
                window.TriggerNext();
                Assert.Equal(2, installAttempts);
                Assert.False(window.HasInstallFailed);
                Assert.NotNull(lastOptions);
                Assert.Equal(Path.GetFullPath(newInstallDir), Path.GetFullPath(lastOptions!.InstallDirectory));
                Assert.Equal(7, window.CurrentPageIndex);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        });
    }

    [Fact]
    public void RemoveUninstallRegistration_DeletesStandardAppAndLegacyInnoSubKeys_InHkcuAndHklm()
    {
        string testRootPath = @"Software\V-Notch-Tests\" + Guid.NewGuid().ToString("N");
        using var testRoot = Registry.CurrentUser.CreateSubKey(testRootPath, writable: true);
        Assert.NotNull(testRoot);

        try
        {
            using var fakeHkcu = testRoot!.CreateSubKey("HKCU", writable: true)!;
            using var fakeHklm = testRoot.CreateSubKey("HKLM", writable: true)!;

            using (var k1 = fakeHkcu.CreateSubKey(SetupOperations.UninstallRegistryPath))
                k1.SetValue("DisplayName", "V-Notch");
            using (var k2 = fakeHkcu.CreateSubKey(SetupOperations.AppRegistryPath))
                k2.SetValue("InstallDir", @"C:\Apps\V-Notch");
            using (var k3 = fakeHkcu.CreateSubKey(SetupOperations.LegacyInnoUninstallKey))
                k3.SetValue("Inno Setup: App Path", @"C:\Legacy\V-Notch");
            using (var k4 = fakeHklm.CreateSubKey(SetupOperations.UninstallRegistryPath))
                k4.SetValue("DisplayName", "V-Notch");
            using (var k5 = fakeHklm.CreateSubKey(SetupOperations.LegacyInnoUninstallKey))
                k5.SetValue("Inno Setup: App Path", @"C:\Legacy\V-Notch");

            SetupOperations.RemoveUninstallRegistration(fakeHkcu, fakeHklm);

            using var verifyHkcuUninstall = fakeHkcu.OpenSubKey(SetupOperations.UninstallRegistryPath);
            using var verifyHkcuApp = fakeHkcu.OpenSubKey(SetupOperations.AppRegistryPath);
            using var verifyHkcuLegacy = fakeHkcu.OpenSubKey(SetupOperations.LegacyInnoUninstallKey);
            using var verifyHklmUninstall = fakeHklm.OpenSubKey(SetupOperations.UninstallRegistryPath);
            using var verifyHklmLegacy = fakeHklm.OpenSubKey(SetupOperations.LegacyInnoUninstallKey);

            Assert.Null(verifyHkcuUninstall);
            Assert.Null(verifyHkcuApp);
            Assert.Null(verifyHkcuLegacy);
            Assert.Null(verifyHklmUninstall);
            Assert.Null(verifyHklmLegacy);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRootPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void RemoveStartupRegistration_DeletesRunValueWithoutCreatingMissingSubKey()
    {
        string testRootPath = @"Software\V-Notch-Tests\" + Guid.NewGuid().ToString("N");
        using var testRoot = Registry.CurrentUser.CreateSubKey(testRootPath, writable: true);
        Assert.NotNull(testRoot);

        try
        {
            // When the Run key does not exist, RemoveStartupRegistration must not create it.
            SetupOperations.RemoveStartupRegistration(testRoot!);
            using (var missingRunKey = testRoot!.OpenSubKey(SetupOperations.StartupRegistryPath))
            {
                Assert.Null(missingRunKey);
            }

            // When the Run key exists, RemoveStartupRegistration removes V-Notch and preserves other values.
            using (var runKey = testRoot.CreateSubKey(SetupOperations.StartupRegistryPath, writable: true)!)
            {
                runKey.SetValue("V-Notch", "\"C:\\Apps\\V-Notch\\V-Notch.exe\"");
                runKey.SetValue("OtherApp", "\"C:\\Apps\\Other\\Other.exe\"");
            }

            SetupOperations.RemoveStartupRegistration(testRoot);

            using (var verifyRunKey = testRoot.OpenSubKey(SetupOperations.StartupRegistryPath))
            {
                Assert.NotNull(verifyRunKey);
                Assert.Null(verifyRunKey!.GetValue("V-Notch"));
                Assert.Equal("\"C:\\Apps\\Other\\Other.exe\"", verifyRunKey.GetValue("OtherApp"));
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRootPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void ExecuteUninstallCleanupSteps_ContinuesRemainingStepsWhenIndividualStepsThrow()
    {
        var executedSteps = new List<string>();

        SetupOperations.ExecuteUninstallCleanupSteps(
            @"C:\Users\Test\AppData\Local\Programs\V-Notch",
            validateCleanupStartInfo: () =>
            {
                executedSteps.Add("validate");
                throw new InvalidOperationException("Simulated start info failure");
            },
            stopRunningInstances: () =>
            {
                executedSteps.Add("stop");
                throw new Win32Exception("Simulated process stop failure");
            },
            removeStartup: () =>
            {
                executedSteps.Add("startup");
                throw new UnauthorizedAccessException("Simulated startup registry failure");
            },
            removeUninstallRegistry: () =>
            {
                executedSteps.Add("registry");
                throw new UnauthorizedAccessException("Simulated uninstall registry failure");
            },
            removeShortcuts: () =>
            {
                executedSteps.Add("shortcuts");
                throw new IOException("Simulated shortcut removal failure");
            },
            removeAppData: dir =>
            {
                executedSteps.Add("appdata:" + Path.GetFileName(dir));
                throw new IOException("Simulated appdata failure");
            },
            scheduleDirectoryCleanup: dir =>
            {
                executedSteps.Add("schedule:" + Path.GetFileName(dir));
            });

        Assert.Equal(
            ["validate", "stop", "startup", "registry", "shortcuts", "appdata:V-Notch", "schedule:V-Notch"],
            executedSteps);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught != null)
        {
            ExceptionDispatchInfo.Capture(caught).Throw();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort temp directory cleanup in tests.
        }
    }
}
