# Feature view ownership

`MainWindow` composes the feature controls and coordinates notch navigation, native
window placement, compact notifications and transitions between views. Feature
controls do not reference `MainWindow` or resolve services from `App.Services`.

| Feature | Owner | Inputs | Shell integration |
| --- | --- | --- | --- |
| Audio mixer | `AudioMixerView` | `AudioMixerViewModel`, lazy mixer/volume services, presentation context | Preferred-height requests and output-device changes |
| Countdown | `CountdownView` and `CountdownController` | `TimerViewModel`, dispatcher callback | Completion notification and keyboard-input requests |
| Weather | `WeatherWidget` | `WeatherModule`, effective online-weather setting | Visibility and module lifecycle |
| Media playback | `MediaPlaybackView` | `MediaViewModel`, `IMediaDetectionService` | Expanded-layout context and compact-visualizer buffering notification |

The media view owns transport controls, marquee, progress tracking, seek guards,
spring rendering and timeline animations. Artwork shared between compact and
expanded notch transitions, lyrics and canvas composition still belong to the
shell/presenters. Moving these remaining shell features is separate work; splitting
a class into additional partial files does not create an ownership boundary.

Controls stop visual work when hidden and release subscriptions, schedules and
mouse capture when disposed. The countdown remains active while its view is hidden;
its monotonic tracker, not UI frame frequency, determines completion.

## Scheduling

`UiScheduledTimer` is a dispatcher-bound schedule, not an independent native timer.
All schedules on a dispatcher share one wake-up timer. Deadlines use `Stopwatch`;
missed intervals do not accumulate a callback backlog. Stopping, changing an
interval or disposing invalidates callbacks already queued on the dispatcher.
The shared wake-up stops when no deadline is pending.

Rendering stays on `CompositionTarget.Rendering`. Countdown tracking uses a
cancellable `PeriodicTimer`. Z-order maintenance listens for foreground events,
with at most two delayed reassertions during a requested transition; it has no
permanent polling watchdog. D3D-image presentation retains its specialized retry
timer because that lifecycle is tied to the rendering surface.

## Async boundaries

WinRT URI launches and face detection are awaited. Use
`SmartThumbnailCropService.GetSmartCropRectAsync` for inference/face framing;
the synchronous crop API remains suitable for lightweight saliency cropping.
Process exit waiting in Spotlight and setup is asynchronous.

Resource owners must await `DisposeAsync` before deleting backing files or exiting.
The synchronous disposal fallback initiates shutdown without blocking the dispatcher.
Application shutdown awaits clipboard, media, settings, GPU and log drains.
An unhandled terminating CLR callback cannot await shutdown; `CrashReporter`
persists its fatal report synchronously and normal logging is best-effort.

Native capture-event waits and nonblocking semaphore probes are not Task waits.
Clipboard PIN derivation, DPAPI protection, rate limiting, fixed-time comparisons
and temporary-buffer zeroing are unchanged.
