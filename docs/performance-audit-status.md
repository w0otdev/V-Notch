# Performance audit remediation

Reference: `performance_bottlenecks_audit.md`, supplied 2026-11-02. Scope: issues verified against current code. The reference's RAM, VRAM and timing estimates are not measurements of this build.

| Area | Disposition |
| --- | --- |
| GPU glass | WGC uses the leased GPU texture directly when shader-readable, with bounded SRV reuse. DXGI copies dirty/moved rectangles and skips source rebuild for changes outside the lens. Native refraction renders the active crop instead of the maximum animation envelope. Pyramids shrink after a sustained small view and are released on session reset. Clear / D3D9 transfers cover the active and previous rectangles, with first-use initialization. Earlier FPS, borderless capture, stable backbuffer and lazy pooling fixes remain. |
| Magnifier fallback | Capture window and callback buffers follow the requested desktop region. Crop coverage is validated. A timed-out stop retains a lease and schedules cleanup after the pump exits; stop calls and duplicated wait handles are synchronized. This deprecated fallback remains only for machines where the GPU path is unavailable. |
| Raw input | A hidden message window on a dedicated STA thread processes mouse packets. Only left-click events reach WPF. Registration is already scoped to expanded interactions. No blocking low-level mouse hook was added. |
| WPF effects | Shadow is already a separate silhouette sibling: the audit's subtree claim is obsolete. Eight named zero-radius shell blurs detach while idle through `ActiveBlur`; animation state remains available. Clipboard action backdrops are collapsed and release their VisualBrush when hidden. Settings limits per-item blur surfaces to six in each cascade while preserving motion. The data-bound privacy text blur remains attached to preserve its binding behavior. |
| Recording | OBS caches executable identity and file-ownership evidence, with expiry and PID/start-time identity. Historical negative log checks are amortized; Bandicam retains prompt checks. OBS log reading uses 16 KiB chunks instead of a log-sized byte/char rent. Restart Manager remains the authoritative ownership check; file timestamps alone cannot prove recording. |
| Translation | Removed startup model preparation. Manual mode installs no automatic WinEvent/UIA hooks; the explicit shortcut still works. One bounded decode loop owns one LLamaContext per inference, avoiding StatelessExecutor 0.27's constructor context. |
| Artwork / inference | Decode longest edge is 512. Four decoded images are reused by encoded content hash; their streams release encoded arrays after OnLoad. Crops are detached from source images and bounded by 8 MiB / 24 entries. Immutable fingerprints and small analysis samples are reused weakly. Explicit GC, CRT heap minimization and working-set eviction were removed. Different analysis resolutions remain where algorithms require them; native inference work from other changes is preserved. |
| Spotlight / AI | Local disk scanning waits for an indexed query and is skipped when Everything is available, including authoritative zero results. App index lifetime is 30 minutes. Streamed Markdown retains completed blocks and rebuilds only its active tail; final formatting occurs once. Thinking shimmer clocks are removed on dismissal. A single huge unfinished Markdown block can still require tail parsing; this is not claimed to be constant-time rendering. |
| Privacy | PID-family cache limit is 2048 with expired / oldest-entry eviction instead of clearing every entry. One lazily acquired pooled process snapshot is shared by recorder and capability checks. A snapshot remains necessary to detect newly launched legacy recorders; its frequency was not blindly reduced. |
| Startup | Settings and Spotlight windows are created at first explicit open. Startup no longer loads their native surfaces / Settings search index or prewarms disk indexes. Cross-language Settings indexing starts when its search box receives focus, and its index is released when Settings closes. |
| Clipboard | Detached viewport previews retain their intentional 8 MiB budget to prevent repeated scrolling decodes. New bitmap captures carry their pixel fingerprint into storage, avoiding PNG re-decode. Duplicate image checks reuse stored image fingerprints and stream other files. Pixel hashing processes 16 rows per CopyPixels call. Original images and private vault behavior are preserved. |
| Canvas / WebView | Production Canvas downloads once to a bounded local MP4 cache (16 MiB per file, 64 MiB / eight files). Loop polling is 100 ms. The standby player is paused except for the short seamless-loop crossfade; the claim of two continuously playing decoders is inaccurate. Login removes the per-resource COM interception; existing consent cancellation disposes WebView2 when revoked. |
| Audio | includeIcons=false skips extraction, with a one-time icon upgrade on demand. The detailed mixer pass enriches the first snapshot without a second CoreAudio enumeration. Exact executable-path matching skips unrelated process names before OpenProcess. |
| Webcam | Stop drops pinned array references after any active frame copy finishes, while queued UI frames retain valid leases. Restart allocates only when needed. Alpha conversion remains necessary for non-premultiplied / alpha-ignored input; removing it would change opacity. |
| Render / noise | Progress, visualizer and tray scroll updates honor configured FPS and elapsed time. Static expanded glass can enter idle pacing. Existing hidden-view / settled-animation unsubscription and FPS-aware marquee are retained. White-noise Pause requests a native fade without joining its audio thread on WPF. |

Validation logs are under `artifacts/audit-*.log` (ignored local build artifacts). Native shader probes use synthetic readback only; live capture stays on the GPU. Tests do not establish an app-wide RAM reduction or eliminate the need for a new interactive allocation trace. No unsupported promise of <0.05 ms/frame or 20-50x speedup is made.

Validation on this workspace:

- Broad regression selection: 2,180 passed, six desktop-dependent skips, two failures. The synthetic scrolling test was updated to advance at the configured FPS; the isolated tray-dismissal retry passed. The final related selection passed all 65 cases.
- Earlier resource and UI selections passed 118 and 107 cases respectively. These overlap the broad selection and are not additional unique coverage.
- After deferring Settings search indexing, all 165 Settings / search / resource tests passed. The animated attach / detach blur regression also passed.
- Native GPU probe passed 120 alternating frames through a persistent D3D9 surface, blur / dispersion / alpha fixtures, live WGC exclusion / borderless capture and live 30-to-120 capture-interval changes.
- The probe now reads the actual persistent D3D9 destination for synthetic fixtures, checks cleared pixels outside shrinking crops, and verifies first-use full initialization before partial transfers. The 24-resize probe retains eight allocations / 44.17 MiB allocation traffic.
- Hidden production MainWindow presented native glass; its compact capture was 513 x 188 pixels, and 100 clip-bounds reads allocated zero bytes in the probe.
- Continuous-wakeup FPS probe changed from 19.9 to 32.2 WPF submissions/sec for targets 30 and 120. This validates the cap / setting change; it does not demonstrate 120 displayed FPS.
- Isolated Debug build and WPF probe build completed with zero warnings / errors. The running desktop application was not restarted by this work.

Upstream context ownership was checked against [LLamaSharp 0.27's source](https://github.com/SciSharp/LLamaSharp/blob/v0.27.0/LLama/LLamaStatelessExecutor.cs). Desktop dirty rectangle semantics follow [Microsoft's Desktop Duplication documentation](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api).

## Low blur and frame handoff follow-up (2026-11-02)

The previous linear blur mapping rounded 5% to 1–3 physical pixels at common DPIs. MainWindow and Spotlight now share a quadratic response with fractional strength throughout the managed/native GPU frame: 5% is 0.07 DIP, multiplied by DPI without rounding. The maximum remains 28 DIP. The native frame still occupies 536 bytes; its blur slot is now a validated float. The CPU fallback skips blur buffers when every box radius is zero.

GPU completion uses an optional D3D11 fence, with a 4 ms timeout request on the capture worker before dispatching presentation. The worker resets stale notifications and checks the current fence value after every wake. Presentation uses that fence directly; older drivers retain the event-query fallback. D3DImage gets up to 1 ms to acquire its writable buffer, avoiding a whole missed composition tick for a short handoff. No GPU completion wait runs on the UI thread. Windows scheduling can extend timeout requests; they are not hard real-time deadlines. Fence semantics follow [Signal](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_3/nf-d3d11_3-id3d11devicecontext4-signal) and [SetEventOnCompletion](https://learn.microsoft.com/en-us/windows/win32/api/d3d11_3/nf-d3d11_3-id3d11fence-seteventoncompletion).

The debug footer distinguishes dispatcher-wide UI rendering callbacks from the main notch's completed glass submissions. Glass submission latency includes capture/render, GPU completion, dispatcher queuing, and D3DImage.Unlock. These measurements exclude DWM/display latency; static backdrops may correctly report idle.

Validation is recorded in `artifacts/glass-polish-*.log`:

- 214 regression cases passed, six desktop-dependent tests skipped. This includes fractional blur/DPI, native frame layout, compositor lock balancing, and separate UI/glass counters.
- Synthetic GPU step-edge fixtures at sigma 0.07–0.14 produced 2.9–10.8% of the edge bleed of the previous 1-pixel kernel. Zero blur preserves the sharp edge. Crossing pyramid boundaries by ±0.01 changed mean pixels by at most 0.04/255.
- 120 alternating frames passed through the persistent D3D9 surface without black/stale frames. A deliberately stale completion event exercised timeout and recovery. The query fallback and live borderless WGC capture also passed.
- A visible 320 × 64 WPF probe with continuous wake-ups submitted 30.0 FPS at target 30 and 115.2 FPS at target 120. At 120, render-to-Unlock latency was 0.61 ms median / 1.06 ms p95. Earlier visible runs before the handoff fix reached about 31–33 FPS at target 120 with roughly 16 ms median latency. These are probe results on this machine, not a guarantee of displayed FPS or heavy-view performance.
- The production MainWindow probe retained 5% blur as sigma 0.0875 at DPI 125%, presented native glass, and allocated zero bytes for 100 clip-bounds reads. Debug and probe builds completed without warnings or errors.

## Animation pacing and interruption (2026-11-03)

Playback progress and seek springs now use the existing phase-preserving `AnimationFrameClock` with WPF composition timestamps. Duplicate callbacks for the same presentation are ignored, and targets such as 120 FPS on a 144 Hz display retain their deadline remainder. Seek timeout uses a monotonic stopwatch.

Spring curves are prepared once in C++ and interpolated by WPF, avoiding a native call and repeated transcendental math for every easing evaluation. The initial linear-table implementation shared about 128 KiB for the two built-in curves; the memory follow-up below reduces this to 16 KiB. Custom curves are owned by their instances. Endpoints and the existing overshoot are preserved. The scalar native export remains available for differential validation.

The paired, alternating benchmark in `SpringEasingPerformanceTests` measured 105.8 to 35.7 ns/evaluation for damping 0.82 (2.97x) and 54.6 to 35.1 ns for damping 1 (1.56x), using 21 samples of 100,000 calls after warmup. Maximum sampled error for the built-in curves was below 2.7e-7, with no steady-state managed allocation. These are easing microbenchmarks, not an app-wide FPS measurement. Results are in `artifacts/animation-native-tests.log`; rerun with `dotnet test Tests/VNotch.Tests.csproj -c Release --filter FullyQualifiedName~SpringEasingPerformanceTests --logger "console;verbosity=detailed"`.

Icon reversals preserve both glyphs' effective opacity and scale, guard both completion callbacks, and release finished clocks. Reduced-motion icon switches complete immediately. Content cancellation holds animated transform/effect values in the same instances, so references to named transforms remain valid. Media expansion and collapse begin at the rendered opacity and translation rather than jumping to their rest values. Regression tests cover interruption, repeated reversal, final visibility, transform identity, and deterministic 45/60/120 FPS scheduling across different composition rates.

The Release build, including the native DLL and shader build targets, completed successfully. The related regression selection passed 160/160 tests with no skips; results are in `artifacts/animation-regressions.log`. The focused interruption/native-easing selection passed 20/20 cases. These selections overlap and are not additive coverage totals.

## Animation memory follow-up (2026-11-03)

Morphing icons reuse their point collections. Each collection is detached from the live path while its coordinates change and reattached once, avoiding both per-frame collection allocation and per-point propagation through the geometry tree. Duplicate paints at the same animation progress reuse the computed geometry. Frozen input geometry can be shared directly. Completed, instantly replaced, and unloaded morphs release their sampling buffers and clocks, with a generation guard against stale completion callbacks.

The two-contour morph benchmark measures 3,848–3,849 managed bytes/frame in the preserved pre-memory renderer versus 536 bytes/frame after point reuse, about 86% less allocation. This includes the drawing harness and excludes native/GPU allocations. The historical renderer is retained in `Tests/Fixtures/BeforeMemoryMorphingSettingsIcon.cs`. Timings varied with shared-machine load; this does not establish an app-wide FPS increase or a working-set reduction of the same percentage.

Native spring preparation now builds 256 cubic Hermite segments per curve. Two shared coefficient arrays occupy 16,384 bytes versus 131,088 bytes for the previous dense tables, an 87.5% payload reduction. Frame evaluation remains allocation-free. Against 100,001 analytic samples per curve, maximum error was below 3.7e-8 for the built-in curves and 7.6e-8 across the five tested damping ratios. Duration, easing parameters, contour count, blur strength, and frame-rate settings are unchanged by this follow-up.

Validation includes 72 exact pixel comparisons against the pre-memory renderer at 96/120/144 DPI, including interrupted morphs, plus lifetime checks using a software-rendered WPF host. Focused tests passed 37/37 cases; measurements are in `artifacts/animation-memory-paired.log`. These tests exercise the CPU rendering path but are not a measurement on physical low-end hardware.

The final Release build and related regression selection passed 321/321 cases with no skips, covering animation, navigation, Settings, Spotlight, morph handoff, and native spring contracts. Results are in `artifacts/animation-memory-regressions.log`.

## Follow-up report triage with Ponytail (2026-11-03)

Reference: the subsequently supplied `performance_bottlenecks_report.md`. Its recommendations are audit inputs, not instructions to weaken durability or change visuals. This pass makes three verified changes:

- MusicVisualizer computes bar layout and settlement once per animation tick and passes the same stack-backed result to change detection and drawing. Direct repaint APIs still compute fresh data. The native algorithm, snapped bounds, gradients, morphs, capture recovery checks, and timing are unchanged.
- Settings no longer creates blur effects or blur clocks for cascade items beyond the existing six-item blur budget. Those items clean up after their last visible fade/slide; the six blurred items still clean up after blur. Visible durations, delays, easing, and opacity are unchanged.
- NotchSettings clamps and scalar equality use managed math instead of per-number P/Invoke. Native exports remain available. Differential tests preserve tolerance, signed infinity, NaN, and invalid-range fallback behavior.

| Report area | Disposition in this pass |
| --- | --- |
| 1. GPU copy / passes | Current WGC path already leases shader-readable textures. Blur levels are dynamic, and unchanged source data can be reused; 13 passes is not an unconditional frame cost. DXGI retains full desktop history so moving the crop after a capture timeout still has valid pixels. No crop-only copy was applied without a moving-crop/rotation probe. |
| 2. Cache / blur / clip | Removed unused Settings tail blur work. Existing shared frozen clips and idle blur detachment remain. Spotlight bitmap caching versus animated blur requires a paired GPU trace; no visual cache policy was changed on the report's assertion alone. |
| 3. Visualizer / particles | Removed duplicate visualizer calculation in the tick path. Endpoint recovery remains. Particle density, motion, and DSP are unchanged; native per-sample calls are not assumed to be managed P/Invoke transitions. |
| 4. Rendering loops | Earlier phase-preserving progress scheduling remains. No blind FPS reduction or removal of an allegedly unused ViewModel. Selection auto-scroll and rewind formatting were not re-profiled here. |
| 5. UIAutomation | No change. Browser/selection timeouts and cross-process traces need a separate reproduction. |
| 6. Process enumeration / WinRT wait | No change. The Sleep(1) WinRT wait exists; replacement needs completion/cancellation and callback-lifetime tests. |
| 7. System monitor / diagnostics | SystemMonitor startup is already lazy for its expanded widget or debug view. Continued sampling after first use and width animation remain candidates for profiling. A naive ScaleTransform changes rounded caps, so it was not substituted under the unchanged-visual constraint. |
| 8. Audio mixer / volume | Ordinary 500 ms volume polling compares process IDs, not StructureKey. Existing UI patching remains. Extending volume-session caching needs an invalidation policy that preserves active-session selection; no stale-target cache was introduced. |
| 9. Media update lock | Not changed or re-profiled; requires session/timeline ordering and dispatcher-latency traces. |
| 10. Artwork / inference | Earlier bounded decode/crop/sample caches remain. No inference lock removal or detection-quality change. Model and FaceDetector lifetime were not re-profiled. |
| 11. Clipboard I/O | Durability barriers and original images remain intact. Earlier fingerprint propagation avoids some repeated decoding. No claim of removing all large-image STA work. |
| 12. Search / Markdown | Earlier completed-block rendering and lazy indexing remain. A returned token array owns its data; using stack scratch before returning an array is not a zero-allocation guarantee. No blanket AST/token/visual-tree rewrite. |
| 13. Settings | Fixed scalar P/Invoke overhead. Backup and persistence ordering are unchanged. |

Targeted validation passed 75 existing cases, then 25 focused cases including new scalar differential and cascade lifetime checks. The final Release regression selection passed 451/451 cases without skips; these selections overlap. Logs are under `artifacts/ponytail-report-*.log`. Ponytail review checked bugs, safety, scale, targeted tests, speed/lean, and debt, with no remaining findings in these changed paths. This pass has no new shortcut ceiling; the five existing shortcut markers found in the source all include upgrade triggers. No app-wide FPS, RAM, or GPU improvement is inferred from static review alone.
