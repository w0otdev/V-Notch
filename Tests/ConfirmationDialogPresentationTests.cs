using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.Windows;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ConfirmationDialogPresentationTests
{
    [Theory]
    [InlineData(ConfirmationDialog.DialogIcon.Warning, false, null)]
    [InlineData(ConfirmationDialog.DialogIcon.Question, true, "fixture details")]
    [InlineData(ConfirmationDialog.DialogIcon.Trash, true, "fixture details")]
    [InlineData(ConfirmationDialog.DialogIcon.Error, true, "https://fixture.invalid/help")]
    [InlineData(ConfirmationDialog.DialogIcon.Info, false, "file:///C:/fixture")]
    public void DialogOptionsChooseTheirIconTextBadgeAndActionStyle(ConfirmationDialog.DialogIcon icon, bool danger, string? detail) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateResources();
        var options = new ConfirmationDialog.DialogOptions("Fixture title", "Continue fixture", "Cancel fixture", icon,
            danger ? ConfirmationDialog.DialogStyle.Danger : ConfirmationDialog.DialogStyle.Normal, detail, danger ? "Fixture badge" : null);
        var dialog = CreateDialog(null, "Fixture message", options);
        try
        {
            Assert.Equal("Fixture title", dialog.Title);
            Assert.Equal("Fixture message", dialog.MessageText.Text);
            Assert.Equal("Continue fixture", dialog.ConfirmButton.Content);
            Assert.Equal("Cancel fixture", dialog.CancelButton.Content);
            Assert.Equal(danger ? Visibility.Visible : Visibility.Collapsed, dialog.BadgeBorder.Visibility);
            Assert.Equal(detail == null ? Visibility.Collapsed : Visibility.Visible, dialog.DetailCard.Visibility);
            if (detail != null) Assert.Equal(detail, dialog.DetailText.Text);
            if (detail?.StartsWith("https:", StringComparison.Ordinal) == true) Assert.Same(Cursors.Hand, dialog.DetailCard.Cursor);
            else Assert.NotSame(Cursors.Hand, dialog.DetailCard.Cursor);
            Assert.NotNull(dialog.DialogIconPath.Data);
            if (icon is ConfirmationDialog.DialogIcon.Error or ConfirmationDialog.DialogIcon.Info)
            {
                Assert.Null(dialog.DialogIconPath.Fill);
                Assert.NotNull(dialog.DialogIconPath.Stroke);
                Assert.Equal(2, dialog.DialogIconPath.StrokeThickness);
            }
            else
            {
                Assert.NotNull(dialog.DialogIconPath.Fill);
                Assert.Null(dialog.DialogIconPath.Stroke);
            }
            if (danger) Assert.Same(dialog.FindResource("DangerButton"), dialog.ConfirmButton.Style);
            dialog.Show();
            await WpfFrameWaiter.UntilAsync(() => dialog.DialogCard.Opacity == 1 && dialog.CardScale.ScaleX == 1 && dialog.CardTranslate.Y == 0, "confirmation dialog entrance", ct);
            Invoke(dialog, "CancelButton_Click", dialog, new RoutedEventArgs());
            await WpfFrameWaiter.UntilAsync(() => !dialog.IsVisible, "confirmation dialog dismissal", ct);
            Assert.False(dialog.Confirmed);
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("confirm", true)]
    [InlineData("cancel", false)]
    [InlineData("enter", true)]
    [InlineData("escape", false)]
    [InlineData("close", false)]
    public void ConfirmationAndKeyboardDismissalAnimateOnceAndKeepTheFirstResult(string action, bool expected) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateResources();
        var owner = new BackgroundWindow { Width = 100, Height = 60 };
        owner.Show();
        var dialog = CreateDialog(owner, "Fixture message", new());
        try
        {
            Assert.Same(owner, dialog.Owner);
            Assert.Equal(Loc.Get("dialog.confirm.title"), dialog.TitleText.Text);
            Assert.Equal(Loc.Get("dialog.confirm"), dialog.ConfirmButton.Content);
            dialog.Show();
            await WpfFrameWaiter.UntilAsync(() => dialog.DialogCard.Opacity == 1, "confirmation ready", ct);
            if (action is "enter" or "escape")
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, Environment.TickCount, action == "enter" ? Key.Enter : Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
                Invoke(dialog, "Window_KeyDown", dialog, args);
                Assert.True(args.Handled);
            }
            else if (action == "close") dialog.Close();
            else Invoke(dialog, action == "confirm" ? "ConfirmButton_Click" : "CancelButton_Click", dialog, new RoutedEventArgs());
            Invoke(dialog, expected ? "CancelButton_Click" : "ConfirmButton_Click", dialog, new RoutedEventArgs());
            Assert.Equal(expected, dialog.Confirmed);
            await WpfFrameWaiter.UntilAsync(() => !dialog.IsVisible, "confirmation exit", ct);
            Assert.Equal(expected, dialog.Confirmed);
        }
        finally { dialog.Close(); owner.Close(); }
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateResources() => new("en", false, configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static void Invoke(ConfirmationDialog dialog, string method, params object?[] args) => typeof(ConfirmationDialog).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, args);
    private static ConfirmationDialog CreateDialog(Window? owner, string message, ConfirmationDialog.DialogOptions options)
    {
        var dialog = ConfirmationDialog.Create(owner, message, options);
        dialog.Opacity = 0;
        dialog.Topmost = false;
        BackgroundTestWindows.ProtectInput(dialog);
        return dialog;
    }
}
