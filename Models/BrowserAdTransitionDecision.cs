namespace VNotch.Models;

internal readonly record struct BrowserAdTransitionDecision(
    bool ShouldHold,
    DateTime TransitionStartedUtc,
    bool WasTransitionActive);
