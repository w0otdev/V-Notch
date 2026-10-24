# Audit remediation — 2026-10-24

Scope: the supplied Vietnamese full-codebase audit, excluding `V-NOTCH-CI-001`.
Findings were checked against current source before changes. The working tree
already contained substantial application, test and CI work when remediation
started; those changes were preserved.

| Finding | Resolution |
| --- | --- |
| PRIV-001 | Clipboard image HTTP uses the shared artwork privacy policy, including cancellation when permission is revoked. Existing Content-Length and streaming byte limits remain at 20 MiB; the report's unbounded-download claim was stale. |
| PRIV-002 | Browser URL consent is held in memory and denied before settings load or after recovery from invalid settings. No scanner request reads JSON. Artwork consent already used the live fail-closed network policy. |
| PRIV-003 | `AppPaths` unifies local stores under `V-Notch`. Normal startup migrates legacy `VNotch` files without overwriting conflicts or following reparse points. Conflicts are retained under `legacy-migration-conflicts`; locked originals stay in place for retry. Uninstall removes both roots. Periodic cleanup intentionally preserves user history/models; the report confused that policy with full data removal. |
| LOGIC-001 | Hotkey status belongs to each controller instance. Settings reads the active shell's controllers through callbacks. Disposing an old instance cannot clear another instance's status. |
| LOGIC-002 | Unreadable encrypted chat history is quarantined before a new store can save. Transient read/rename failures preserve the original and can recover on a later load. |
| LOGIC-003 | Quota cooldowns and video caches use credential hashes. A replacement key resumes immediately, including the same video; late old-key responses cannot block or overwrite the new credential's result. Duration-enrichment quota errors are handled too. |
| LOGIC-004 | VolumeService observes default render-device changes through NAudio. Notification callbacks invalidate state; ordinary service operations release/reopen the endpoint outside the callback. |
| WIN32-001 | Spotlight keyboard hooks run on an owned thread with its own message pump. Escape state resets between visibility sessions; UI actions are queued with lifecycle guards. |
| MEM-001 | Mixer cache owns its retained session and disposes it with its cached volume. Existing scan loops already disposed sessions, and `MMDevice` already owns manager disposal. NAudio's `Dispose` implementation does not promise immediate native RCW release; the report's stronger claim is not established. |
| MEM-002 | Artwork decode caps the longest edge, including portrait images. |
| MEM-003 | WMI command-line access tracks and releases the explicit locator, services, process, property-set and property objects in reverse ownership order. The report's claim that direct dynamic property access necessarily leaks hidden RCWs was not proven. |
| CONC-001 | Subtitle API-key publication uses `Interlocked.Exchange` and `Volatile.Read`. |
| CONC-002 | Removed the scanner's static FileSystemWatcher and filesystem side effects; settings publish consent in memory. |
| TEST-001 | D3D lock tests use a frame-target interface instead of modifying private WPF runtime fields. Extracted state owners have direct behavior tests. App-private UI integration harnesses remain where they exercise view behavior. |
| ARCH-001 | Per the agreed scope, independent owners hold Settings edit-session snapshots, Clipboard selection and Invisible-mode save/reentrancy state. Existing controllers/presenters remain; there is no wholesale rewrite or claim that the three views contain no shared presentation state. |
| PROTO-001 | Validated media compatibility data ships as `Assets/MediaProtocols.json` with embedded recovery defaults. Normal app updates carry changes; casual users have no technical setup step. Existing automatic Spotify hash discovery and YouTube key refresh remain. See [compatibility data](docs/MEDIA_PROTOCOL_CONFIGURATION.md). |
| LIC-001 | The current dependency and lockfiles already select NotifyIcon 2.0.1, whose exact package/source license is MIT. Corrected stale 1.1.0 notices and ship its complete MIT notice. Earlier CPOL distributions remain a separate historical matter. See [package evidence](docs/NOTIFYICON_REPLACEMENT_PLAN.md). |
| DOC-001 | Added `RECORDING_DETECTION.md`. The other three reported missing documents already exist; retained their links and corrected outdated NotifyIcon/privacy facts. |
| CI-001 | Excluded by request; no changes to test/coverage CI wiring in this remediation. |

Validation at handoff: Release builds of the application/test project and
uninstaller passed with zero warnings/errors. The broad non-desktop test run
found three additional failures: SoundCloud title separators, blur darkening
rounding at factor 0.78, and Unicode-number literal verification. All three have
code fixes and regression coverage, but those final edits have not been rebuilt
or retested before the user requested wrap-up. Full test output is available in
`.artifacts/audit-remediation-tests.log`; a clean full-suite result is not claimed.
`git diff --check` passed. Added explicit ignore exceptions so the compatibility,
license-assessment, model-rating documents and complete tray license can be
included in source control instead of remaining hidden by the `/docs` rule.

Hardware-specific desktop behavior still depends on Windows/audio/GPU support;
automated tests are not evidence of manual checks on every device or provider.
