using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Services;
using VNotch.Tests.Fakes;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowTimerInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("01:30", 90)]
    [InlineData("1h 2m 3s", 3723)]
    [InlineData("2d", 172800)]
    [InlineData("1s", 5)]
    public void EditingCommitsCustomTimeAndRestoresTheDisplay(string input, int seconds) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var timer = ((ShellViewModel)window.DataContext).Timer;
        Invoke(window, "StartTimerEditing");
        Assert.True(Field<bool>(window, "_isEditingTimer"));
        Assert.Equal(timer.DisplayText, window.CountdownWidget.CountdownInput.Text);
        window.CountdownWidget.CountdownInput.Text = input;
        Invoke(window, "CommitTimerEditing", true);
        Assert.False(Field<bool>(window, "_isEditingTimer"));
        Assert.Equal(TimeSpan.FromSeconds(seconds), timer.Duration);
        Assert.Equal(timer.Duration, timer.Remaining);
        Assert.False(timer.IsRunning);
        await WpfFrameWaiter.UntilAsync(() => window.CountdownWidget.CountdownInput.Visibility == Visibility.Collapsed,
            "timer input dismissal after commit", ct);
        Assert.Equal(Visibility.Visible, window.CountdownWidget.CountdownDisplay.Visibility);
    });

    [Theory]
    [InlineData("invalid", true, true)]
    [InlineData("invalid", false, false)]
    [InlineData("", true, false)]
    public void InvalidInputRetriesOrCancelsWithoutChangingTheDuration(string input, bool retry, bool stillEditing) => SharedStaTestRunner.RunAsync(async ct =>
    {
        await using var fixture = CreateFixture();
        var window = fixture.Window;
        var timer = ((ShellViewModel)window.DataContext).Timer;
        TimeSpan original = timer.Duration;
        Invoke(window, "StartTimerEditing");
        window.CountdownWidget.CountdownInput.Text = input;
        Invoke(window, "CommitTimerEditing", retry);
        Assert.Equal(stillEditing, Field<bool>(window, "_isEditingTimer"));
        Assert.Equal(original, timer.Duration);
        if (stillEditing)
        {
            Assert.Equal(input.Length, window.CountdownWidget.CountdownInput.SelectionLength);
            await WpfFrameWaiter.UntilAsync(() => window.CountdownWidget.CountdownInputTranslate.X == 0, "invalid timer input shake", ct);
            Invoke(window, "CancelTimerEditingInstant");
        }
        else
        {
            await WpfFrameWaiter.UntilAsync(() => window.CountdownWidget.CountdownInput.Visibility == Visibility.Collapsed,
                "invalid timer input dismissal", ct);
        }
        Assert.Equal(Visibility.Visible, window.CountdownWidget.CountdownDisplay.Visibility);
        Assert.False(Field<bool>(window, "_isEditingTimer"));
    });

    [Fact]
    public void StartPauseResetAndStepButtonsControlTheSameCountdown() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var timer = ((ShellViewModel)window.DataContext).Timer;
        Click(window, "CountdownPlus_Click", window.CountdownWidget.CountdownPlusBtn);
        Invoke(window, "StopCountdownRepeat");
        Assert.Equal(TimeSpan.FromMinutes(26), timer.Duration);
        Click(window, "CountdownMinus_Click", window.CountdownWidget.CountdownMinusBtn);
        Invoke(window, "CountdownBtn_MouseLeaveOrUp", window.CountdownWidget.CountdownMinusBtn, new EventArgs());
        Assert.Equal(TimeSpan.FromMinutes(25), timer.Duration);
        Click(window, "CountdownStart_Click", window.CountdownWidget.CountdownStartBtn);
        Assert.True(timer.IsRunning);
        Click(window, "CountdownPlus_Click", window.CountdownWidget.CountdownPlusBtn);
        Click(window, "CountdownMinus_Click", window.CountdownWidget.CountdownMinusBtn);
        Assert.Equal(TimeSpan.FromMinutes(25), timer.Duration);
        Click(window, "CountdownStart_Click", window.CountdownWidget.CountdownStartBtn);
        Assert.False(timer.IsRunning);
        Click(window, "CountdownStart_Click", window.CountdownWidget.CountdownStartBtn);
        Assert.True(timer.IsRunning);
        Click(window, "CountdownReset_Click", window.CountdownWidget.CountdownResetBtn);
        Assert.False(timer.IsRunning);
        Assert.Equal(timer.Duration, timer.Remaining);
        Click(window, "CountdownStart_Click", window.CountdownWidget.CountdownStartBtn);
        Click(window, "CountdownDisplayPanel_Click", window.CountdownWidget.CountdownDisplayPanel);
        Assert.False(timer.IsRunning);
        Assert.True(Field<bool>(window, "_isEditingTimer"));
        window.CountdownWidget.CountdownInput.Text = "02:00";
        Click(window, "CountdownStart_Click", window.CountdownWidget.CountdownStartBtn);
        Assert.True(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(2), timer.Duration);
        Click(window, "CountdownReset_Click", window.CountdownWidget.CountdownResetBtn);
        Invoke(window, "StartTimerEditing");
        Click(window, "CountdownReset_Click", window.CountdownWidget.CountdownResetBtn);
        Assert.False(Field<bool>(window, "_isEditingTimer"));
    });

    [Fact]
    public void InstantEditingCancellationClearsAllInputAnimations() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        Invoke(window, "StartTimerEditing");
        window.CountdownWidget.CountdownInput.Text = "unfinished";
        Invoke(window, "CancelTimerEditingInstant");
        Assert.False(Field<bool>(window, "_isEditingTimer"));
        Assert.Equal(Visibility.Collapsed, window.CountdownWidget.CountdownInput.Visibility);
        Assert.Equal(Visibility.Visible, window.CountdownWidget.CountdownDisplay.Visibility);
        Assert.Equal(1, window.CountdownWidget.CountdownDisplay.Opacity);
        Assert.Equal(0, window.CountdownWidget.CountdownDisplayTranslate.Y);
        Assert.False(window.CountdownWidget.CountdownInput.HasAnimatedProperties);
        Assert.False(window.CountdownWidget.CountdownInputTranslate.HasAnimatedProperties);
        Assert.False(window.CountdownWidget.CountdownInputScale.HasAnimatedProperties);
        Invoke(window, "CancelTimerEditingInstant");
        Invoke(window, "CancelTimerEditing");
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        settings => settings.EnableLocalOnlyMode = true,
        services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static void Click(MainWindow window, string method, object sender)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Invoke(window, method, sender, args);
        Assert.True(args.Handled);
    }
    private static void Invoke(MainWindow window, string method, params object?[] arguments) =>
        typeof(Controls.CountdownView).GetMethod(method, Private)!.Invoke(window.CountdownWidget, arguments);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(Controls.CountdownView).GetField(name, Private)!.GetValue(window.CountdownWidget)!;
}
