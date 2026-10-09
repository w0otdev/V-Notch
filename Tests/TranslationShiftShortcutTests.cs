using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationShiftShortcutTests
{
    [Theory]
    [InlineData(10u)]
    [InlineData(uint.MaxValue - 200u)]
    public void ContinuousShiftSpamTriggersOnceUntilAQuietPeriodEvenAcrossFocusChanges(uint start)
    {
        var detector = new ShiftTapDetector();
        int triggers = 0;
        for (uint tap = 0; tap < 40; tap++)
        {
            uint key = tap % 2 == 0 ? 0xA0u : 0xA1u;
            var window = new IntPtr(tap < 2 || tap >= 20 ? 1 : 2);
            Assert.False(detector.Process(key, true, unchecked(start + tap * 100), window));
            if (detector.Process(key, false, unchecked(start + tap * 100 + 40), window)) triggers++;
        }
        Assert.Equal(1, triggers);
        // A new deliberate double-tap works after the burst has ended.
        Assert.False(detector.Process(0xA0, true, unchecked(start + 4600), new(1)));
        Assert.False(detector.Process(0xA0, false, unchecked(start + 4640), new(1)));
        Assert.False(detector.Process(0xA0, true, unchecked(start + 4700), new(1)));
        Assert.True(detector.Process(0xA0, false, unchecked(start + 4740), new(1)));
    }

    [Fact]
    public void SecondTapMayFinishOutsideTheGapWindow()
    {
        var detector = new ShiftTapDetector();
        detector.Process(0xA0, true, 10, new(1));
        detector.Process(0xA0, false, 60, new(1));
        detector.Process(0xA0, true, 520, new(1));
        Assert.True(detector.Process(0xA0, false, 700, new(1)));
    }

    [Fact]
    public void TimestampWrapStillRecognizesTwoTaps()
    {
        var detector = new ShiftTapDetector();
        detector.Process(0xA0, true, uint.MaxValue - 100, new(1));
        detector.Process(0xA0, false, uint.MaxValue - 50, new(1));
        detector.Process(0xA0, true, 50, new(1));
        Assert.True(detector.Process(0xA0, false, 100, new(1)));
    }

    [Fact]
    public async Task HookOwnsAMessageLoopAndDisposalStopsIt()
    {
        using var shortcut = new TranslationShiftShortcut(() => { });
        var field = typeof(TranslationShiftShortcut).GetField("_dispatcher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var thread = (Thread)typeof(TranslationShiftShortcut).GetField("_thread", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(shortcut)!;
        Assert.True(SpinWait.SpinUntil(() => field.GetValue(shortcut) != null, 3000));
        var dispatcher = (System.Windows.Threading.Dispatcher)field.GetValue(shortcut)!;
        Assert.NotEqual(Environment.CurrentManagedThreadId, dispatcher.Thread.ManagedThreadId);
        Assert.True(await dispatcher.InvokeAsync(() => true).Task.WaitAsync(TimeSpan.FromSeconds(3)));
        shortcut.Dispose();
        Assert.True(thread.Join(3000));
    }

    [Fact]
    public async Task ManualSelectionRetriesTransientEmptyResultsAndDeliversOnce()
    {
        int captures = 0, delivered = 0;
        var selection = new TranslationSelection(1, "selected", new System.Windows.Rect(0, 0, 30, 20), new(1), false);
        await TranslationSelectionService.CaptureManualAsync(() => true,
            () => ++captures < 3 ? null : selection,
            value => { Assert.Same(selection, value); delivered++; }, () => Task.CompletedTask);
        Assert.Equal(3, captures);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task ManualSelectionNeverDeliversAfterFocusOrRequestChanges()
    {
        bool current = true;
        await TranslationSelectionService.CaptureManualAsync(() => current,
            () => { current = false; return new(1, "old", new System.Windows.Rect(0, 0, 30, 20), new(1), false); },
            _ => Assert.Fail("Stale selection was delivered"), () => Task.CompletedTask);
    }

    [Fact]
    public async Task ManualSelectionRetriesAreBounded()
    {
        int captures = 0;
        await TranslationSelectionService.CaptureManualAsync(() => true,
            () => { captures++; return null; }, _ => Assert.Fail("Empty selection was delivered"), () => Task.CompletedTask);
        Assert.Equal(3, captures);
    }

    [Fact]
    public async Task MissingSelectionReportsFailureOnceInsteadOfSilentlyDroppingShortcut()
    {
        int failures = 0;
        await TranslationSelectionService.CaptureManualAsync(() => true, () => null,
            _ => Assert.Fail("No selection exists"), () => Task.CompletedTask, () => failures++);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task FocusChangeSuppressesMissingSelectionPopup()
    {
        await TranslationSelectionService.CaptureManualAsync(() => false, () => null,
            _ => Assert.Fail("Stale selection"), () => Task.CompletedTask,
            () => Assert.Fail("Must not open over another application"));
    }

    [Theory]
    [InlineData(0xA0u, 0xA0u)]
    [InlineData(0xA1u, 0xA1u)]
    [InlineData(0xA0u, 0xA1u)]
    [InlineData(0xA1u, 0xA0u)]
    public void TwoReleasedShiftTapsTriggerOnce(uint first, uint second)
    {
        var detector = new ShiftTapDetector();
        Assert.False(detector.Process(first, true, 10, new(1)));
        Assert.False(detector.Process(first, false, 60, new(1)));
        Assert.False(detector.Process(second, true, 150, new(1)));
        Assert.True(detector.Process(second, false, 200, new(1)));
        Assert.False(detector.Process(second, true, 250, new(1)));
        Assert.False(detector.Process(second, false, 300, new(1)));
    }

    [Fact]
    public void HeldAndRepeatedShiftDoesNotTrigger()
    {
        var detector = new ShiftTapDetector();
        Assert.False(detector.Process(0xA0, true, 10, new(1)));
        Assert.False(detector.Process(0xA0, true, 20, new(1)));
        Assert.False(detector.Process(0xA0, false, 400, new(1)));
        Assert.False(detector.Process(0xA0, true, 450, new(1)));
        Assert.False(detector.Process(0xA0, false, 500, new(1)));
    }

    [Theory]
    [InlineData(0x41u, false)] // Shift+A
    [InlineData(0x25u, false)] // Shift+Left selecting text
    [InlineData(0xA0u, true)] // another modifier held / injected event
    public void OtherKeysAndModifiersCancelTheSequence(uint key, bool blocked)
    {
        var detector = new ShiftTapDetector();
        detector.Process(0xA0, true, 10, new(1));
        detector.Process(0xA0, false, 20, new(1));
        Assert.False(detector.Process(key, true, 30, new(1), blocked));
        Assert.False(detector.Process(0xA0, true, 50, new(1)));
        Assert.False(detector.Process(0xA0, false, 60, new(1)));
    }

    [Theory]
    [InlineData(1, 800u)]
    [InlineData(2, 200u)]
    public void SlowTapsOrChangingApplicationsCannotTrigger(int window, uint time)
    {
        var detector = new ShiftTapDetector();
        detector.Process(0xA0, true, 10, new(1));
        detector.Process(0xA0, false, 20, new(1));
        detector.Process(0xA1, true, time, new(window));
        Assert.False(detector.Process(0xA1, false, time + 50, new(window)));
    }
}
