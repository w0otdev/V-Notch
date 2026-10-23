# Recording detection

V-Notch's privacy indicators combine Windows capability-use information with
local evidence from supported recording applications. They describe evidence the
app can observe; an unlit indicator does not establish that capture is absent.

| Source | Evidence used | Limits |
| --- | --- | --- |
| Windows capture | `graphicsCaptureProgrammatic` and `graphicsCaptureWithoutBorder` capability records, correlated with running consumers | Capture paths that do not report these capabilities can be missed. |
| Bandicam | A running `bdcam.exe` owns its latest local recording output from Bandicam settings | Historical output alone is ignored. Unsupported paths and inaccessible files are not evidence. |
| OBS Studio | A running `obs64.exe` or `obs32.exe` owns a log containing recording, streaming or replay-buffer start/stop markers | Standard and portable layouts are checked. Initial/backlog reads are bounded; old markers, changed log formats or denied access can prevent detection. |
| FFmpeg | A running FFmpeg command line identifies a supported screen-capture input | Command lines can be inaccessible across account or privilege boundaries. An arbitrary FFmpeg process is not sufficient. |

The providers run locally, reuse a process snapshot and fail independently. A
failed recorder probe does not prevent other privacy indicators from updating.
V-Notch does not inject into recorder processes or install capture hooks for
this detection. Microphone activity additionally uses audio-flow evidence; camera
and other sensor state follow Windows capability information.

Indicators can lag a recording transition by the sampling interval. Protected
processes, nonstandard recorder builds, unsupported applications and Windows
reporting differences can cause missed or stale evidence. Check the recording
application and Windows privacy controls when certainty is needed.

Implementation: `Services/PrivacyIndicatorService.cs`,
`Services/ScreenRecordingProbe.cs`, `Services/BandicamRecordingProbe.cs`,
`Services/ObsRecordingProbe.cs`, `Services/FfmpegRecordingProbe.cs`.
