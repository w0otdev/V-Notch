using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class AppNotificationTests
{
    [Fact]
    public void NativeFilterNormalizesTextExtractsAumidAndDeduplicates()
    {
        AppNotificationService.ResetNativeState();
        long now = 1_700_000_000_000L;

        bool accepted = AppNotificationService.TryEvaluateAndFormat(
            101,
            now - 500,
            now,
            "com.squirrel.Discord.Discord",
            "",
            "  Alex   Message  ",
            "Hey   there\r\n\r\nAre you free tonight?  ",
            out string appName,
            out string title,
            out string body);

        Assert.True(accepted);
        Assert.Equal("Discord", appName);
        Assert.Equal("Alex Message", title);
        Assert.Equal("Hey there\nAre you free tonight?", body);

        // Same ID must be rejected as duplicate.
        bool duplicateId = AppNotificationService.TryEvaluateAndFormat(
            101,
            now - 200,
            now + 100,
            "com.squirrel.Discord.Discord",
            "Discord",
            "Different Title",
            "Different Body",
            out _,
            out _,
            out _);
        Assert.False(duplicateId);

        // Same content within 8s window must be rejected even with a new ID.
        bool duplicateContent = AppNotificationService.TryEvaluateAndFormat(
            102,
            now,
            now + 1500,
            "com.squirrel.Discord.Discord",
            "Discord",
            "Alex Message",
            "Hey there\nAre you free tonight?",
            out _,
            out _,
            out _);
        Assert.False(duplicateContent);

        // Self-notifications from V-Notch must be rejected.
        bool selfNotif = AppNotificationService.TryEvaluateAndFormat(
            103,
            now,
            now,
            "VNotch.App",
            "V-Notch",
            "Update Ready",
            "Click to install",
            out _,
            out _,
            out _);
        Assert.False(selfNotif);

        // Stale notifications (> 30s old) must be rejected.
        bool staleNotif = AppNotificationService.TryEvaluateAndFormat(
            104,
            now - 45_000,
            now,
            "Telegram.TelegramDesktop",
            "Telegram",
            "Old Message",
            "Should not pop up",
            out _,
            out _,
            out _);
        Assert.False(staleNotif);

        // Duplicate app name in title must be collapsed so app name is not shown twice.
        bool dedupAppTitle = AppNotificationService.TryEvaluateAndFormat(
            105,
            now,
            now,
            "com.google.antigravity",
            "Antigravity",
            "Antigravity",
            "Agent finished: Notch Notification Support Request",
            out string dedupApp,
            out string dedupTitle,
            out string dedupBody);
        Assert.True(dedupAppTitle);
        Assert.Equal("Antigravity", dedupApp);
        Assert.Equal("Agent finished: Notch Notification Support Request", dedupTitle);
        Assert.Equal(string.Empty, dedupBody);
    }

    [Fact]
    public void NativeBannerMetricsAlwaysExpandBeyondCompactPill()
    {
        var singleLine = AppNotificationService.ComputeBannerMetrics(
            collapsedWidth: 230,
            collapsedHeight: 34,
            expandedWidth: 580,
            cornerRadiusCollapsed: 10,
            cornerRadiusExpanded: 20,
            isDynamicIsland: false,
            reduceMotion: false,
            configuredDurationMs: 5000,
            title: "New message",
            body: "Short preview");

        var multiLine = AppNotificationService.ComputeBannerMetrics(
            collapsedWidth: 230,
            collapsedHeight: 34,
            expandedWidth: 580,
            cornerRadiusCollapsed: 10,
            cornerRadiusExpanded: 20,
            isDynamicIsland: false,
            reduceMotion: false,
            configuredDurationMs: 5000,
            title: "Build completed",
            body: "All 42 unit tests passed.\nArtifact uploaded to release channel.");

        Assert.True(singleLine.TargetWidth >= 360);
        Assert.True(singleLine.TargetHeight >= 80);
        Assert.True(multiLine.TargetHeight > singleLine.TargetHeight);
        Assert.Equal(1, multiLine.HasBody);
        Assert.Equal(1, multiLine.IsMultilineBody);
    }

    [Fact]
    public void NotchExpandsIntoToastBannerAndCollapsesBackToRest() => SharedStaTestRunner.RunAsync(async ct =>
    {
        const double restW = 230;
        const double restH = 34;

        await using var fixture = new GreetingAcceptanceTests.MainWindowFixture(
            "en",
            greeting: false,
            configureSettings: s =>
            {
                s.Width = (int)restW;
                s.Height = (int)restH;
                s.EnableLocalOnlyMode = true;
                s.ShowSystemNotifications = true;
                s.NotificationDuration = 4000;
            },
            configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));

        var window = fixture.Window;

        string notepadPath = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var notif = new AppNotificationInfo
        {
            Id = 777,
            AppId = notepadPath,
            AppName = "Notepad",
            Title = "Sarah Connor",
            Body = "Meet me at the cybernetics lab at 8 PM."
        };

        window.ShowAppNotification(notif);

        Assert.True(window.IsAppNotificationBannerActive);
        Assert.Equal(Visibility.Visible, window.AppNotificationBanner.Visibility);
        Assert.Equal(Visibility.Collapsed, window.CollapsedContent.Visibility);
        Assert.Equal("Notepad", window.AppNotificationAppNameText.Text);
        Assert.Equal("Sarah Connor", window.AppNotificationTitleText.Text);
        Assert.Equal("Meet me at the cybernetics lab at 8 PM.", window.AppNotificationBodyText.Text);
        Assert.Equal("SC", window.AppNotificationAvatarInitialsText.Text);
        Assert.Equal("now", window.AppNotificationTimeText.Text);
        Assert.Equal(Visibility.Visible, window.AppNotificationProgressTrack.Visibility);
        Assert.Equal("SR", AppNotificationService.ExtractInitials("Sam Rivera", "Messages"));
        Assert.Equal("AG", AppNotificationService.ExtractInitials("The agent has a question for you:", "Antigravity"));
        Assert.NotNull(window.AppNotificationIconImage.Source);
        Assert.Equal(Visibility.Visible, window.AppNotificationIconImage.Visibility);
        Assert.Equal(Visibility.Collapsed, window.AppNotificationDefaultIcon.Visibility);

        double initialBannerContentWidth = window.AppNotificationBanner.Width;

        await WpfFrameWaiter.UntilAsync(
            () => window.NotchBorder.Width > restW + 100 &&
                  window.NotchBorder.Height > restH + 35 &&
                  Math.Abs(window.AppNotificationBanner.Opacity - 1.0) < 1e-4,
            "app notification toast banner expanded on notch",
            ct);

        await WpfFrameWaiter.UntilAsync(() => window.NotchBorder.Width == (double)typeof(MainWindow)
                .GetField("_appNotificationBaseWidth", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!,
            "notification entrance reaches its resting width", ct);
        double baseNotchWidth = window.NotchBorder.Width;
        double baseNotchHeight = window.NotchBorder.Height;
        window.AnimateAppNotificationHover(true);

        await WpfFrameWaiter.UntilAsync(
            () => window.AppNotificationHoverSurface.Opacity >= .03 && window.HoverGlow.Opacity == 0 &&
                  window.NotchBorder.Width == baseNotchWidth &&
                  window.NotchBorder.Height == baseNotchHeight &&
                  Math.Abs(window.NotchScale.ScaleX - 1.0) < 1e-4 &&
                  Math.Abs(window.AppNotificationBannerScale.ScaleX - 1.0) < 1e-4 &&
                  Math.Abs(window.AppNotificationBanner.Width - initialBannerContentWidth) < 1e-4,
            "notification hover lights the shell without floating or resizing content",
            ct);

        window.DismissAppNotificationBanner();

        await WpfFrameWaiter.UntilAsync(
            () => !window.IsAppNotificationBannerActive &&
                  window.AppNotificationBanner.Visibility == Visibility.Collapsed &&
                  Math.Abs(window.NotchBorder.Width - restW) < 1.0 &&
                  Math.Abs(window.NotchBorder.Height - restH) < 1.0,
            "app notification toast banner collapsed back to resting notch",
            ct);
    });
}
