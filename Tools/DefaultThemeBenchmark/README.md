# Default theme render updates

## Changes

- AnalogClock keeps the same three hand transforms in WPF's retained drawing. A normal 33 ms tick changes only their angles, instead of invalidating the UIElement and recording a new drawing. Sweep timing, initial delay, geometry, brushes, date and hand formulas are unchanged.
- Resize, date visibility/day changes and DPI changes rebuild the relevant drawing. Date rollover and moving between DPI settings still refresh the face.
- MainWindow collapses the unused glass material container in the default theme. Hidden glass layers no longer receive per-animation corner updates or clip geometry allocations. Enabling glass makes the container visible, synchronizes corners and refreshes its clip before presentation.

## Reproduce

From the repository root on Windows:

```powershell
dotnet run --project Tools/DefaultThemeBenchmark -c Release -- artifacts/default-theme.json
dotnet test Tests/VNotch.Tests.csproj -c Release --filter "FullyQualifiedName~AnalogClock|FullyQualifiedName~LiquidGlassSpotlightTests"
```

Run performance measurements without tests/builds running concurrently. BaselineAnalogClock.cs freezes the prior implementation; only its type/namespace and deterministic-time entry point are changed, and the shared brush is replaced with the identical frozen #C8C8C8 brush.

## Method and scope

Measured on Ryzen 7 5800H, Windows, .NET 8.0.29, Release. Each case warms both paths for 500 ms, then measures seven rounds with alternating execution order. Reported times are median round means. Drawing-update rounds use 2,000 frames; offscreen raster rounds use 100. Each frame advances time by the original 33 ms cadence. Timing excludes waiting for the dispatcher timer.

The update benchmark records the original drawing on each tick versus updating the production retained transforms. The raster benchmark additionally clears and renders a RenderTargetBitmap. The latter measures synchronous offscreen WPF rasterization, not GPU/display frame presentation. The DPI column denotes render-target DPI, with a 92 DIP clock face.

This is a component benchmark, **not whole-theme/app FPS**. CPU drawing updates improve substantially and allocations fall, but the absolute saving is around 1.5-2 microseconds per tick. That cannot establish a 20-50% increase in displayed FPS. Offscreen raster results show no consistent improvement; every result, including regressions, is retained below. The hidden-glass work removal is covered by integration tests and is not included in the clock performance numbers.

## Results

### Run 1

| Target DPI | Includes raster | Before us | After us | Throughput change | Allocated B/frame before -> after |
|---:|:---:|---:|---:|---:|---:|
| 96 | False | 2.454 | 0.492 | +399.0% | 1936 -> 72 |
| 96 | True | 543.945 | 529.550 | +2.7% | 2544 -> 680 |
| 120 | False | 2.040 | 0.470 | +333.7% | 1936 -> 72 |
| 120 | True | 668.969 | 722.341 | -7.4% | 2544 -> 680 |
| 144 | False | 2.158 | 0.492 | +338.3% | 1936 -> 72 |
| 144 | True | 663.797 | 617.514 | +7.5% | 2544 -> 680 |
| 192 | False | 2.002 | 0.461 | +334.6% | 1936 -> 72 |
| 192 | True | 643.203 | 630.729 | +2.0% | 2544 -> 680 |

### Run 2

| Target DPI | Includes raster | Before us | After us | Throughput change | Allocated B/frame before -> after |
|---:|:---:|---:|---:|---:|---:|
| 96 | False | 2.354 | 0.469 | +401.8% | 1936 -> 72 |
| 96 | True | 532.515 | 535.697 | -0.6% | 2544 -> 680 |
| 120 | False | 2.070 | 0.489 | +323.0% | 1936 -> 72 |
| 120 | True | 566.731 | 587.111 | -3.5% | 2544 -> 680 |
| 144 | False | 1.974 | 0.476 | +314.9% | 1936 -> 72 |
| 144 | True | 589.877 | 581.977 | +1.4% | 2544 -> 680 |
| 192 | False | 2.196 | 0.514 | +326.9% | 1936 -> 72 |
| 192 | True | 634.928 | 627.868 | +1.1% | 2544 -> 680 |

Ordinary drawing updates allocate 72 instead of 1,936 bytes per tick (96.3% less). The clock's rasterized output is unchanged; no resolution, effect quality or frame cadence is reduced.

## Validation

The benchmark verifies 256 exact before/after pixel comparisons per run across timestamps, resizing, date toggles and four raster DPIs. Automated rendering tests cover another 384 comparisons, including midnight, plus explicit invalidation after date visibility and DPI changes. The MainWindow integration test verifies hidden glass state stays untouched while default corners animate, and current corners/clip are restored when revealed. The existing live MainWindow glass pixel test also passes.

The combined clock/glass suite passed 23 tests. After the final retained-command simplification and DPI invalidation hook, the focused clock/default-window suite passed 8/8 tests. No live default-theme display FPS uplift has been measured.
