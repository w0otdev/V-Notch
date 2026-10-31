using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using VNotch.Presenters;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class GradientAndAnimationGuardTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "V-Notch.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root containing V-Notch.csproj.");
    }

    [Fact]
    public void CountdownPresenter_DoesNotUseLinearGradientBrush()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "Presenters", "CountdownPresenter.cs"));
        Assert.DoesNotContain("LinearGradientBrush", source, StringComparison.Ordinal);

        FieldInfo? idleBrushField = typeof(CountdownPresenter).GetField("_startIdleBrush", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(idleBrushField);

        var idleBrush = idleBrushField!.GetValue(null) as Brush;
        var solidIdle = Assert.IsType<SolidColorBrush>(idleBrush);
        Assert.True(solidIdle.IsFrozen);
    }

    [Fact]
    public void ClipboardCardViewModel_DoesNotUseLinearGradientBrush()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "ViewModels", "ClipboardCardViewModel.cs"));
        Assert.DoesNotContain("LinearGradientBrush", source, StringComparison.Ordinal);

        FieldInfo? sharedCardBrushField = typeof(ClipboardCardViewModel).GetField("SharedCardBrush", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(sharedCardBrushField);

        var cardBrush = Assert.IsType<SolidColorBrush>(sharedCardBrushField!.GetValue(null));
        Assert.True(cardBrush.IsFrozen);
    }

    [Fact]
    public void SettingsWindow_AnimatedStarPath_EliminatesGradientAndRespectsReduceMotionAndFrameRate()
    {
        string repoRoot = FindRepoRoot();
        string xaml = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.xaml.cs"));

        int donatingStart = xaml.IndexOf("x:Name=\"PanelDonating\"", StringComparison.Ordinal);
        int starTitleIdx = xaml.IndexOf("x:Name=\"StarRepoTitle\"", donatingStart, StringComparison.Ordinal);
        Assert.True(donatingStart >= 0 && starTitleIdx > donatingStart);

        string starSection = xaml.Substring(donatingStart, starTitleIdx - donatingStart);
        Assert.DoesNotContain("LinearGradientBrush", starSection, StringComparison.Ordinal);
        Assert.DoesNotContain("BlurEffect", starSection, StringComparison.Ordinal);
        Assert.DoesNotContain("FrameworkElement.Loaded", starSection, StringComparison.Ordinal);
        Assert.Contains("Fill=\"#FFFFC83D\"", starSection, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"StarPulseStoryboard\"", starSection, StringComparison.Ordinal);
        Assert.Contains("Timeline.DesiredFrameRate=\"24\"", starSection, StringComparison.Ordinal);
        Assert.Contains("IsVisibleChanged=\"PanelDonating_IsVisibleChanged\"", starSection, StringComparison.Ordinal);

        Assert.Contains("UpdateAnimatedStarStoryboard()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("!AnimationConfig.ReduceMotion && PanelDonating.Visibility == Visibility.Visible", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindow_ManualCityTextBoxIsWired_AndTranslationProgressAutomationNameIsLocalized()
    {
        string repoRoot = FindRepoRoot();
        string xaml = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(repoRoot, "Windows", "SettingsWindow.xaml.cs"));

        int manualCityStart = xaml.IndexOf("x:Name=\"ManualCityTextBox\"", StringComparison.Ordinal);
        int manualCityEnd = xaml.IndexOf("</TextBox>", manualCityStart, StringComparison.Ordinal);
        Assert.True(manualCityStart >= 0 && manualCityEnd > manualCityStart);

        string manualCityBlock = xaml.Substring(manualCityStart, manualCityEnd - manualCityStart);
        Assert.Contains("TextChanged=\"ManualCityTextBox_TextChanged\"", manualCityBlock, StringComparison.Ordinal);

        Assert.DoesNotContain("AutomationProperties.Name=\"Model download progress\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loc.Get(\"settings.translation.modelDownloadProgress\")", codeBehind, StringComparison.Ordinal);
    }
}
