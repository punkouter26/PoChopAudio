# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**PoChopAudio is a WinUI 3 desktop app.** It splits recordings into one clip per take, and takes
head-shot photos and strips their backgrounds. Everything runs on the machine: there is no server,
no HTTP, and nothing is uploaded.

It did not start that way. The repo used to host an ASP.NET Minimal API plus a Blazor WASM client,
and some of the architecture is still shaped by that history — the vertical slices, the chop job
store, the `Outcome<T>` return type. Those all survive because they earned their place, not because a
server is coming back. **`PoChopAudio.API` and `PoChopAudio.Client` were deleted deliberately; do
not reintroduce them.**

## Governance documents

- [NET_RULES.md](NET_RULES.md) — the standing rules (naming, layout, testing). Written for this
  author's projects generally, so **its §4 Blazor UI section and the web half of §6 no longer apply
  here**; §1 also says trunk is `master` while this repo's trunk is `main`. Everything else holds.
- [AGENT.md](AGENT.md) — the living architecture doc. Records what was actually built and why,
  including a "Deliberately absent" section. Update it when architecture changes.
- [README.md](README.md) — user-facing behaviour of the chop knobs.

## Commands

```powershell
# Restore + build (Release) + run all tests. Add -Run to also build and launch the desktop app.
./SCRIPTS/setup.ps1
./SCRIPTS/setup.ps1 -Run

# Fetch the ~4.4 MB u2netp.onnx cutout model into src/PoChopAudio.WinUI/Content/Models (idempotent)
./SCRIPTS/download-models.ps1
```

Plain dotnet, against the `.slnx` solution:

```powershell
dotnet build PoChopAudio.slnx -c Release
dotnet test PoChopAudio.slnx                              # both test projects
dotnet test tests/PoChopAudio.Unit --filter "FullyQualifiedName~SegmentDetectorTests"
```

`TreatWarningsAsErrors` is on solution-wide — a warning fails the build.

### Building and running the desktop app

The solution-wide build does **not** produce a runnable app: the WinUI project needs an explicit
platform and RID.

```powershell
dotnet build src/PoChopAudio.WinUI/PoChopAudio.WinUI.csproj -c Release -p:Platform=ARM64 -r win-arm64
```

Then run the exe from **`bin`, never `publish`**:

```
src\PoChopAudio.WinUI\bin\ARM64\Release\net10.0-windows10.0.19041.0\win-arm64\PoChopAudio.WinUI.exe
```

`dotnet publish` silently drops `PoChopAudio.WinUI.pri`, the app's own resource index, and without
it the app dies at startup with a stowed exception (`0xC000027B`) inside `Microsoft.UI.Xaml.dll`.
The build output has the `.pri` plus every self-contained Windows App SDK file.

**The app is self-contained** (`WindowsAppSDKSelfContained` + `SelfContained`). The pinned Windows
App SDK 1.6 runtime is not installed on most machines, and an unpackaged app without it fails to
launch with `0x80670016`. Shipping the runtime in the output folder avoids a machine-wide install.

## Architecture in one pass

.NET 10 / C# 15. Three source projects, two test projects, vertical slices.

```
src/PoChopAudio.Services/   Host-agnostic engine room. No UI, no ASP.NET. All the real logic.
src/PoChopAudio.Shared/     Contracts and const limits used by both other projects.
src/PoChopAudio.WinUI/      The app: XAML views, view models, camera, audio devices, file pickers.
```

The dependency direction is one-way: `WinUI → Services → Shared`. `Services` must never reference
`WinUI` or take a dependency on any UI or hosting framework — that constraint is what keeps the
logic testable without a window, and it is why the two test projects can cover it directly.

### Where the real logic is

The interesting, test-covered parts are pure and I/O-free — put new logic there, not in view models:

- [SegmentDetector.cs](src/PoChopAudio.Services/Chop/SegmentDetector.cs) — the split algorithm.
  It does not pick a fixed threshold: it sweeps every gate from `noiseFloor + 3 dB` to `peak - 3 dB`
  in 0.5 dB steps and keeps the centre of the *widest band of gates that agree on the expected
  count*. Read the Detection section of AGENT.md before touching it.
- [ClipExporter.cs](src/PoChopAudio.Services/Chop/ClipExporter.cs) — WAV slicing and `UniqueStems`,
  the case-insensitive de-duplication that makes the flat batch ZIP safe on Windows. Also the
  two-pass export: normalization needs the whole clip measured before the first sample can be
  written, so it reads the slice twice rather than buffering it.
- [ClipProcessor.cs](src/PoChopAudio.Services/Chop/ClipProcessor.cs) — export gain and fade maths,
  no I/O. The three guards (silence, ceiling, max gain) live in `DecideGain`.
- [LoudnessMeter.cs](src/PoChopAudio.Services/Chop/LoudnessMeter.cs) — ITU-R BS.1770-4. The
  K-weighting biquads come from the analog prototype, **not** the RBJ cookbook shelf, which is a
  different filter and reads ~0.25 LU low.
- [EdgeProcessor.cs](src/PoChopAudio.Services/Cutout/EdgeProcessor.cs) — mask threshold, morphology,
  feather, alpha multiplier, in that order.
- [HeadFinder.cs](src/PoChopAudio.Services/Cutout/HeadFinder.cs) — crops to the head alone. u2netp
  is a saliency model, not a face model, so its alpha bounding box always includes the shoulders.
  This reads per-row mask widths: the head widens to a peak, narrows to the neck, then the
  shoulders flare back out. Cut at the neck. A head-only photo never flares and keeps full height.
- [ImageDecoder.cs](src/PoChopAudio.Services/Cutout/ImageDecoder.cs) — decode, EXIF auto-rotate,
  Pixel Motion Photo (`.MP.jpg`) trailer strip.

### The two services

`ChopService` and `CutoutService` are the whole feature surface. The view models call them and
nothing else.

They are deliberately different shapes. `ChopService` keeps the **job store**: upload decodes to a
canonical WAV, analyze re-runs only the cheap tuning step against it, and export renders on demand,
so turning a knob does not re-decode the file. Jobs expire after 2 h.

`CutoutService` has no job store at all — `CutOutAsync` decodes, masks, cleans, crops and encodes in
one call. The multi-step version existed to give a browser a handle to refer back to across
stateless requests; in-process the caller already holds the bytes.

Per NET_RULES §2, slices never reference each other; anything two slices need goes in `Shared`.

### Outcome, not exceptions

Service calls return `Outcome<T>` ([Outcome.cs](src/PoChopAudio.Services/Outcome.cs)): a value, or a
domain reason there isn't one (`NotFound`, `Invalid`, `Empty`, `TooLarge`, `UnsupportedMedia`,
`Undecodable`, `EngineUnavailable`). Expected failures travel on the normal return path — an expired
job is not exceptional.

The view models funnel everything into an `ErrorMessage`, so they unwrap with `.OrThrow()`
([ServiceOutcomeExtensions.cs](src/PoChopAudio.WinUI/Services/ServiceOutcomeExtensions.cs)), which
converts an outcome to an exception **at the consumer boundary only**. Do not push that back into
the services.

### Long work must not run on the UI thread

`Analyze`, `GetClip` and the ZIP builders do real DSP and image work. Call them through `Task.Run`
from a view model or the window freezes. This is why the service methods are mostly synchronous —
the caller decides where they run.

## Conventions worth knowing

- **IDs are `readonly record struct`** with `TryParse` (`JobId`, `CutoutJobId`) — no bare `Guid` or
  `string` ids crossing a boundary. Closed sets are enums (`CutoutEngine`).
- **Logging** goes through `[LoggerMessage]` source generators (`ChopLog`, `CutoutLog`). No string
  interpolation in log calls. `Services` uses `Microsoft.Extensions.Logging.Abstractions`; the app
  registers a factory with `services.AddLogging()`.
- **Batch failure is per file.** A file that will not decode is flagged and still included in the
  ZIP; the batch never aborts.
- **Packages are pinned centrally** in [Directory.Packages.props](Directory.Packages.props); project
  files carry bare `<PackageReference Include="..." />` with no version.
- **Scoped XAML, no inline styles.** WinUI 3 ships no `WrapPanel`; there is a small one in
  [Controls/WrapPanel.cs](src/PoChopAudio.WinUI/Controls/WrapPanel.cs) rather than a toolkit
  dependency.

### Optional-capability pattern

`u2netp.onnx` is absent from a fresh clone. The WinUI csproj includes it only
`Condition="Exists(...)"`; when missing, `EnginePicker` drops `OnnxU2Net`, `CutoutService.IsAvailable`
goes false and the Cutout page shows a banner saying so. Cutout tests skip themselves.

Platform codecs are the same shape: M4A/AAC/WMA go through `MediaFoundationReader`.
`AudioDecoder.IsSupportedExtension` reports the list for the running platform.

Follow this pattern for anything else optional: probe, report, degrade — never throw at startup.

### Camera

`CutoutPage` → `CameraService` → `MediaFrameReader`. WinUI 3 has no `CaptureElement` (that was UWP),
so the viewfinder is built from frames pushed into a `SoftwareBitmapSource`. Stills come from the
most recent preview frame rather than `CapturePhotoToStreamAsync`, which fights the frame reader for
the device on many webcams.

The page drops frames while one is still being handed to the UI thread — without that guard the
queue grows without bound and the preview drifts seconds behind the room.

## Test layout

| Project | Scope | State |
| --- | --- | --- |
| `tests/PoChopAudio.Unit` | Pure logic — detector, exporters, decoder, edge processor | 103 tests, all passing. Add here first |
| `tests/PoChopAudio.Integration` | Multi-component behaviour against the real services | 19 tests, all passing |

Both reference `Services` and `Shared` directly. `Integration` used to drive the HTTP pipeline
through `WebApplicationFactory`; when the API was deleted its export-maths and cutout-pipeline tests
were ported to call the services instead, which is closer to what the app actually does.

The camera, the microphone and the WinUI UI itself need real hardware and a real window, and are
**not covered by any automated test**.
