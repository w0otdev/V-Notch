# Screen capture validation

The notch's top-level HWND requests `WDA_EXCLUDEFROMCAPTURE` before its first
visible frame in every theme. The window lifetime owns this requirement;
renderer cleanup, capture hotkeys, and disabling Invisible mode cannot remove it.
Invisible mode also applies exclusion to auxiliary WPF windows.

## Automated coverage

`WindowAnimationPolicyTests` and `LiquidGlassCapturePolicyTests` query the real
Windows `GetWindowDisplayAffinity` value on isolated test windows. They cover:

- Solid and Liquid Glass themes before the first frame.
- Requests from the render worker followed by UI visibility/composition refreshes.
- Missing and replacement native window handles.
- GPU, CPU with exclusion, Magnifier, capture-overlay, and CPU fallback branches.
- Renderer requests to clear exclusion while the notch still exists.
- Invisible mode retaining its override and restoring auxiliary-window preferences.

These tests verify Windows exclusion state and production control flow. They do
not take screenshots or launch each third-party capture utility.

## Keeping the open view during capture

Capture activity freezes automatic fullscreen/idle visibility changes and blocks
focus-loss, outside-click and hover dismissal while a region is being selected.
Foreground notifications, Print Screen / Win+Shift+S, and capture overlays under
the pointer cover both activating and nonactivating capture windows. A short
handoff interval protects queued input after the overlay closes. Normal editor
windows and unrelated fullscreen applications retain their existing behavior.

`MainWindowScreenCaptureTests` exercises the real dismissal handlers for media,
clipboard, timer and audio views with an injected capture-activity probe, including
input queued before/after the overlay changes. `ScreenCaptureActivityTests` covers
overlay classification and handoff expiry; `ScreenCaptureVisibilityTests` verifies
that an already-hidden notch remains hidden and a visible notch is not auto-hidden.
The capture-affinity tests above continue to verify exclusion from the saved image.

## Desktop verification matrix

The following end-to-end checks still require a dedicated desktop session with
the rebuilt application. They were not executed by the background test suite.

| Capture method | Variants to verify |
| --- | --- |
| Print Screen | Clipboard capture and the Windows setting that launches Snipping Tool |
| Win+Print Screen | Saved full-desktop screenshot, including multiple monitors |
| Alt+Print Screen | Active-window capture, with the notch overlapping that window |
| Win+Shift+S / Snipping Tool | Rectangle intersecting part/all of the notch, freeform, window, full-screen |
| Snipping Tool delay | Capture after a delay while the notch is collapsed, expanded, or animating |
| ShareX / Greenshot / Lightshot / Snagit | Full-screen, region, active-window, delayed and scrolling modes when available |
| Other Windows capture clients | GDI/BitBlt, DXGI Desktop Duplication, and Windows.Graphics.Capture clients |

Repeat with solid/glass themes, Invisible mode on/off, CPU fallback, theme changes,
and hide/reopen cycles. The saved image must show the underlying desktop where
the notch was, without a full notch, partial frame, shadow, or black placeholder.
Check the output image itself; a capture selection overlay can show a cached
desktop preview.

With a notch view open before capture, select a region that crosses the notch,
then complete or cancel capture. Verify that the same view remains open, its
contents are retained, and the notch has not been moved to the desktop layer.
Repeat with mouse-leave auto-close, idle hiding, and fullscreen hiding enabled.

Windows exclusion applies to supported OS capture paths. A utility that bypasses
display affinity is not guaranteed to honor it; see Microsoft's
[SetWindowDisplayAffinity documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity).
