# WPF FPS and bounded stress runner

This program hosts the production Release `MainWindow` and service graph in a separate WPF process. It replaces the startup manager with a no-op and supplies a real `SettingsService` using a run-local settings path. It bypasses the main executable's single-instance/update/setup startup path. Fonts are supplied by the harness; the normal App.xaml bootstrap is not used.

The workload is synthetic and stronger than mouse input: it calls the built-in debug view selector and media update methods. Network lookups, Spotlight, weather, smart crop and startup registration are disabled. It does not play/seek media, change volume, start a camera, or modify the user's settings profile. It does use production privacy/audio enumeration services and screen capture for glass.

```powershell
dotnet build Tools/StressRunner/StressRunner.csproj -c Release
$exe = (Resolve-Path 'Tools/StressRunner/bin/Release/net8.0-windows10.0.19041.0/win-x64/StressRunner.exe').Path
$result = Join-Path (Get-Location) 'artifacts/fps-stress/my-default-run'
New-Item -ItemType Directory -Force $result | Out-Null
Start-Process -FilePath $exe -ArgumentList @('default', $result) -WindowStyle Hidden -Wait
python Tools/StressRunner/analyze.py $result
```

Use `liquidglass` instead of `default` for a glass run. The WPF window is deliberately visible, while the helper console is hidden. Run one instance at a time. Paths should not contain spaces when invoking this simple PowerShell example.

Phases: stable media display 15 s; transition requests targeting 50 Hz for 30 s; media update requests targeting 1000/s for 30 s; mixed media/view/theme churn 30 s; contention from a separate bounded CPU process 30 s; active recovery 30 s; collapsed recovery 15 s; synthetic playing state 15 s. The initial stable media and active recovery use a paused MediaInfo (`IsAnyMediaPlaying=true`, `IsPlaying=false`); the final playing phase uses `IsPlaying=true`. There is no real music playback. The media flood changes track/artwork every 20 submitted events, drawing from 24 frozen 256px images.

Targets are not guaranteed delivered rates: Task.Delay and dispatcher capacity affect them. Phase JSON records actual call counts and time including drain. Calls are not completed UI transitions. The production media handler itself posts another dispatcher callback and may discard superseded updates.

The harness caps its own pending queue at 2000 (not the entire app queue), stops new load after a Background-priority probe has waited 15 s, and terminates only on combined prolonged render/probe inactivity, private bytes over 2 GiB, or total elapsed time over 300 s. CPU contention uses up to eight workers and 16 MiB per worker for at most approximately 32 s; those resources belong to a separate process. An earlier exploratory runner terminated at 15/30 s of probe inactivity alone; reports distinguish these runs from the final recovery-capable runner.

FPS is the rate of distinct WPF CompositionTarget.Rendering callbacks measured with Stopwatch, not DWM/displayed frame FPS. Frame percentiles are callback intervals. The callback subscription itself can keep WPF rendering, so collapsed measurements are not natural idle-power measurements. CPU is normalized by logical processor count; the pressure process is excluded. Memory/handles are sampled each second without forced GC. A few minutes cannot prove absence of a long-term leak. Raw logs and JSON remain in the run directory.

For focused verification of the overload patch, run `StressRunner.exe --check-overload`. This does not start the app: it checks the production media queue under 100,000 concurrent submissions plus ordering, disposal, reentrancy and animation state reversal. The UI stress workload directly updates Media/Progress view models and then calls MainWindow; it does not measure the ShellViewModel event subscription separately. After the overload runs, `compare.py` compares their analysis JSON with the original saved runs and writes `OVERLOAD_FIX_REPORT.md`.
