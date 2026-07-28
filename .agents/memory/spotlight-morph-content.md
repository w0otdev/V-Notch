---
name: Spotlight morph vs live content
description: Why Spotlight open/close morphs must never animate shell geometry over a live results panel
---

Rule: any Spotlight shell geometry morph (open, close, reverse) must run over a
content-free or content-frozen shell. Never animate Shell Width/Height while the
results ListBox participates in live layout, and never run the BlurEffect radius
ramp over a populated content surface.

**Why:** Width animation re-measures the whole ListBox subtree every frame
(text trimming, icons), fires SizeChanged → selection-glide updates per frame,
and the blur + drop shadow re-render the full surface per frame on a layered
(per-pixel alpha) window with CacheMode nulled. On weaker GPUs the 560ms
ExponentialEase(7) morph renders only a few frames and reads as "animation
lost" — exactly the bug reported when closing Spotlight with results showing.
The entrance never had the bug because it defers the content reveal until the
morph lands and covers the shell with a notch snapshot.

**How to apply:** The exit freezes ContentRegion (explicit W/H, Left-aligned,
clipped) and collapses it when the 170ms content fade completes; the blur ramp
runs only for empty-bar exits; glide updates are suppressed while `_isClosing`;
the reverse path must restore ContentRegion to auto layout (NaN sizes, Stretch,
unclipped, Visible) BEFORE measuring the reopen target, and ResetContentRegion
clears all freeze state on CompleteHide. If a new morph path is added (e.g. a
different dismiss animation), mirror this: freeze or drop content first, then
animate geometry. Regression test: ExitWithVisibleResults_FreezesContentAndRestoresItOnReverse.
