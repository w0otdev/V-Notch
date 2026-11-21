# Native boundary and runtime contract — 2026-11-16

## Scope and ownership

The host and DLL ship together using ABI **2.0**. The version descriptor lives in
`Native/vnotch_abi.h`. ABI 2 retires `vn_backdrop_composite`,
`vn_translation_compute_character_fades`, and
`vn_translation_selection_can_preserve_recent_snapshot`. External clients that
use these exports require the 1.x DLL.

| Domain | Native ownership | Managed boundary / consumer |
| --- | --- | --- |
| Media policy | `Native/Services/BrowserMediaTransitionPolicy.cpp`, `MediaTimelinePolicy.cpp` | `Interop/MediaPolicyNative.*`; pure policy facades |
| Glass | `Native/Controllers/GpuGlass*`, capture and blur modules | `D3DImageFramePresenter`, `LiquidGlassController`; HRESULT contract |
| Clipboard | `Native/Services/Clipboard/*` | `Services/Clipboard/Native*`; handle/arena ownership |
| UI math | `Native/Controls/*`, animation/layout services | Existing control/presenter adapters; migration remains |
| Inference | `Native/Services/SmartCropInference.cpp` | `SmartThumbnailCropService`; serialized session lifetime |
| Translation | Native translation policy plus LLama runtime | `Services/Translation/*`; separate LLama runtime contract |

New media policy imports belong in `Interop/MediaPolicyNative.*`, not in
UI classes or the policy facades. Native boundary tests prohibit imports and
mutable fields in the two migrated facades. Other existing import owners remain
legacy adapters; this guard does not yet cover the whole repository.

## ABI and errors

The host assembly registers one lazy DLL resolver. Before returning the module
for **any** of its `VNotch.Native[.dll]` imports, it loads the adjacent library and
calls `vn_abi_query`. The descriptor is six fixed-width `uint32_t` fields (24
bytes): descriptor size, major, minor, pointer size, registry-handle encoding
version, and status-table version. Missing/incompatible descriptors fail before
a feature call. A failed validation releases the loader reference; a compatible
module remains loaded for the lifetime of its imports/handles. OS and third-party
DLLs retain their existing resolution. Tests/tools in other assemblies do not
inherit the host's resolver.

The host requires major version 2. The descriptor layout stays fixed. Additive
exports increment the minor version. A host that needs a later minor must
increase its minimum-version check. Changes
to signatures, field offsets, ownership or meaning require a new major and a
coordinated host update. An ABI number is a maintained compatibility promise,
not automatic verification of every function signature. The existing export
inventory test still checks every host import against the actual DLL.

| Status | Native name | Managed name | Meaning |
| --- | --- | --- | --- |
| 0 | `VN_OK` | `NativeStatus.Ok` | Success |
| -1 | `VN_ERR_INVALID_ARG` | `InvalidArgument` | Invalid arguments or registry handle |
| -2 | `VN_ERR_BUFFER_TOO_SMALL` | `BufferTooSmall` | Caller must provide more capacity |
| -3 | `VN_ERR_FAIL` | `Failure` | Operation failed |

`VNotchNative.VN_*` remain source-compatible aliases. Migrated media adapters
check status before consuming output. **Do not interpret HRESULTs, booleans,
counts, or legacy positive sentinels using this table.** In particular glass
uses HRESULT and retains its own success/pending semantics.

Registry-handle encoding v1 is `(generation << 32) | (slot + 1)`, with zero
invalid. Close unpublishes immediately; outstanding leases defer destruction.
Handles are scoped to their creating registry, process and DLL lifetime. The
encoding is not a cross-registry type tag and must never be persisted or passed
to an unrelated API. Borrowed result pointers must not outlive their owner.
Existing raw Win32/COM handles have their API-specific ownership, not this scheme.

## Glass runtime decisions

`ProcessFrame` samples Ghost/Invisible mode once for its initial GPU route choice.
The remaining generation checks reject stale work. Capture affinity continues
to be owned by the window lifecycle, independently of renderer routing.

| Request/result | Action |
| --- | --- |
| GPU + Invisible mode | Native desktop capture (native code chooses DXGI/WGC) |
| GPU + normal mode | Try native per-window compositor |
| Per-window `E_PENDING` | Try existing Magnifier/composite backdrop path |
| Desktop `E_PENDING` or any other failing HRESULT | Presenter failure/recovery; never reinterpret as a usable frame |
| Successful status with no new frame | Keep previous/pending frame and its geometry |
| `S_OK` | Submit, wait briefly on worker for GPU readiness, present only when ready |
| CPU or pixel-upload fallback | Existing Magnifier → exclusion-safe BitBlt/composite behavior |

`GlassGpuResultPolicy` owns render-result classification. `CaptureStatus` now
reports `route=windows|desktop|pixels|waiting` alongside the existing backend,
capture/borderless/fence HRESULTs and consumer stalls. `route` is the last
attempt, not proof of a presented frame. Fence waiting/presentation errors are
handled separately and must not enter the capture fallback classifier.

Automated coverage is not certification of hybrid GPU + recording. Follow
`SCREEN_CAPTURE_VALIDATION.md` on a disposable interactive desktop. Record GPU
adapter, monitor, route/backend/fence diagnostics, Ghost state, recorder, and
whether a frame was actually presented. Repeat monitor moves, sleep/resume,
device loss, Ghost toggles and recording start/stop. Do not enable hosted-runner
flags to bypass the local visible-desktop test restrictions.

## Media policy contract

The two facades accept snapshots and explicit timestamps, and return decisions.
They do not read the clock, scan windows, perform I/O, or dispatch UI work.
`MediaDetectionService` starts its processing/heartbeat loops with `Task.Run`;
metadata enrichment belongs there, with publication delivering completed state.
Simple timeline predicates can remain cheap synchronous operations; adding
browser probing or metadata recovery to them would violate this boundary.

The existing native finite-duration ceiling is **30 days**, inclusive; zero,
negative, larger values and explicit indeterminate reports remain indeterminate.
The previously unused managed 12-hour constant now agrees with that behavior.
The browser ad ceiling remains two minutes and quarantine remains 45 seconds.

## Memory and verification limits

SmartCrop previously constructed the CPU fallback while retaining its failed
DirectML session. It now releases the old session before replacement under the
same inference mutex. Failed CPU initialization leaves an empty session; a later
request can retry without calling ONNX Runtime with a null session. Tests of the
shared replacement primitive check release order, allocation failure and retry;
bundled-model tests exercise actual ONNX inference and idle unloading. This does
not simulate a real DirectML device-loss failure.

LLama translation already unloads before GPU-to-CPU retry and waits for previous
engine disposal. Artwork crop cache has a byte budget and weak references. None
of those facts prove the simultaneous peak of weights, KV context, DirectML,
glass textures, decoder scratch and WPF. No forced GC, working-set trim, automatic
quality reduction or new model download is introduced here.

Use `Tools/Measure-RuntimeMemory.ps1` against a fresh **application** process on
the target 8 GB machine, starting sampling before loading the local model:

```powershell
./Tools/Measure-RuntimeMemory.ps1 -TargetProcessId 1234 `
  -Scenario '8GB; model ID/quantization/context; GPU; Glass; artwork churn' `
  -DurationSeconds 180 -OutputPath artifacts/memory/combined.json
```

Record idle, AI-only, glass/artwork-only, combined first load/prefill, model change,
GPU fallback, and post-unload. Keep identical model/context/settings across
runs; include fresh-process repetitions and long-recording/monitor changes.
The JSON distinguishes OS-maintained **lifetime** peak working set from sampled
interval peaks and private commit. Sampling can miss short private-commit peaks;
working set includes shared resident pages and excludes dedicated VRAM. Pair
this with GPU dedicated/shared-memory measurements and responsiveness, not
managed allocation counts alone. Lifetime peak before the interval is recorded
so earlier activity cannot be mistaken for this scenario's peak.

The sampler smoke test runs an idle PowerShell process solely to verify output.
The development machine has about 32 GB RAM; no 8 GB combined-load pass is claimed.

## Reproduction and evidence

The rebuilt native DLL and managed Release build pass with zero warnings/errors.
The focused run passes 224 tests, zero failures/skips, including descriptor/layout
validation, rejection of a library without a descriptor, incompatible version/
pointer/handle/status cases, browser transitions, timeline, media lifecycle,
glass recovery/capture policy, artwork lifetime, actual bundled-model inference,
and the inventory of 847 host imports. The C++ release-order/failure/retry test
also passes with `/W4 /WX`. These checks do not cover visible desktop integration.

Use isolated outputs when another developer/test process holds the default WPF
intermediate assembly:

```powershell
dotnet build Tests/VNotch.Tests.csproj -c Release `
  --artifacts-path artifacts/native-boundary/build -warnaserror `
  -p:RestoreLockedMode=true -p:NativeFastCompile=true `
  -p:VNotchNativeArtifactPath=D:/CodeShii/V-Notch/artifacts/native-boundary/VNotch.Native.dll
dotnet test Tests/VNotch.Tests.csproj -c Release `
  --artifacts-path artifacts/native-boundary/build --no-build --no-restore `
  --filter 'FullyQualifiedName~NativeBoundaryTests|FullyQualifiedName~NativeServiceContractTests'
```

After the native build, compile/run `Tools/TestSupport/NativeSessionReplacementTests.cpp`
with MSVC using `obj/native/vcvars64.env.cmd` and `/std:c++20 /EHsc /W4 /WX`.
Local evidence is in `artifacts/native-boundary-final-build.log`,
`artifacts/native-boundary/results/native-boundary-final.trx`, and
`artifacts/native-boundary/memory-sampler-smoke.json` (all ignored artifacts).
Rebuild/repackage the DLL with the host: an older DLL without `vn_abi_query` is
intentionally rejected. The checked DLL lives in the isolated artifact directory;
no running application or installed binary was replaced.
