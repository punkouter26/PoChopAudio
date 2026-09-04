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

- [AGENT.md](AGENT.md) — the architecture doc. Records what was built and why, including a
  "Deliberately absent" section. **Read it for the algorithms, distrust it on the plumbing.** Large
  parts predate the desktop move and were never updated: it describes HTTP endpoints
  (`POST /api/chop/upload`), a browser `/headshots` page, an `AudioWorklet` recorder, a
  `SCRIPTS/verify-recorder-wav.js` and `SCRIPTS/verify-camera.js` that do not exist, a
  `CutoutEngine.BrowserOnnx` that no longer ships, and a Chop page "excluded from build" that is in
  fact built and navigable. Its "No face-detection model" claim is also stale — see `IFaceLocator`
  below. The Detection, Export polish, Decoding and meter sections *are* current and are the best
  explanation of the DSP anywhere in the repo. Update it when architecture changes.
- [README.md](README.md) — user-facing behaviour of the chop and export knobs. Current.
- `NET_RULES.md` — the author's standing rules (naming, layout, testing). **Deleted from the repo**;
  read it with `git show f185bd3:NET_RULES.md`, the last commit that carried it. The
  parts that still bind here are §2 (vertical slices never reference each other — anything two
  slices need goes in `Shared`) and §5 (test layout). Its §4 Blazor UI section and the web half of
  §6 no longer apply, and §1 says trunk is `master` while this repo's trunk is `main`.

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
dotnet test tests/PoChopAudio.Unit --filter "FullyQualifiedName~HeadFinderTests.KeepsFullHeightForHeadOnlyPhoto"
```

`TreatWarningsAsErrors` is on solution-wide — a warning fails the build. There is no separate
linter, formatter or analyzer config: the compiler is the whole gate. The one blanket suppression is
`MVVMTK0045` in the WinUI csproj.

### Building and running the desktop app

The solution-wide build does **not** produce a runnable app: the WinUI project needs an explicit
platform and RID, because it is unpackaged and self-contained.

```powershell
dotnet build src/PoChopAudio.WinUI/PoChopAudio.WinUI.csproj -c Release -p:Platform=ARM64 -r win-arm64
```

Then run the exe from **`bin`, never `publish`**:

```
src\PoChopAudio.WinUI\bin\ARM64\Release\net10.0-windows10.0.19041.0\win-arm64\PoChopAudio.WinUI.exe
```

**Check the architecture before you build.** `Platforms` is `x86;x64;ARM64` and the output path
contains whichever you picked, so a wrong guess quietly builds an app you then cannot find.
`setup.ps1` defaults to ARM64 only when `$env:PROCESSOR_ARCHITECTURE` equals `ARM64`, and **that
variable reads `AMD64` inside an emulated x64 shell on an ARM64 machine** — so the default can be
wrong for the machine it is running on. Get the truth from
`[System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture` and pass `-Platform`
explicitly.

`dotnet publish` silently drops `PoChopAudio.WinUI.pri`, the app's own resource index, and without
it the app dies at startup with a stowed exception (`0xC000027B`) inside `Microsoft.UI.Xaml.dll`.
The build output has the `.pri` plus every self-contained Windows App SDK file.

**The app is self-contained** (`WindowsAppSDKSelfContained` + `SelfContained`). The pinned Windows
App SDK 1.6 runtime is not installed on most machines, and an unpackaged app without it fails to
launch with `0x80670016`. Shipping the runtime in the output folder avoids a machine-wide install.

### When the running app misbehaves

There is no console. Logs go to `%LOCALAPPDATA%\PoChopAudio\logs\pochopaudio.log`
([FileLoggerProvider.cs](src/PoChopAudio.WinUI/Services/FileLoggerProvider.cs)), which is the only
window into the camera, the face locator and the cutout pipeline — none of which any test covers.
The file is **deleted and restarted** past 2 MB rather than rotated, so copy it before reproducing
something long.

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
  Each tuning constant carries the reasoning for its current value in a doc comment — read those
  before changing a number.
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
  registers a factory in `App.ConfigureServices` with `services.AddLogging()`.
- **Batch failure is per file.** A file that will not decode is flagged and still included in the
  ZIP; the batch never aborts.
- **Packages are pinned centrally** in [Directory.Packages.props](Directory.Packages.props); project
  files carry bare `<PackageReference Include="..." />` with no version. Adding a package means
  editing both files.
- **Scoped XAML, no inline styles.** WinUI 3 ships no `WrapPanel`; there is a small one in
  [Controls/WrapPanel.cs](src/PoChopAudio.WinUI/Controls/WrapPanel.cs) rather than a toolkit
  dependency.

### Settings, and what persists

Exactly three things survive a restart — theme, default save folder, and whether a batch save may
skip the folder picker — in `%LOCALAPPDATA%/PoChopAudio/settings.json` via `AppSettingsService`.
The chop and cutout knobs are deliberately **not** persisted: a knob belongs to a take, not to the
app. Do not add them without reading the reasoning in AGENT.md.

The Settings page also carries the capability report (`DiagnosticsReport`) — the one place that
says out loud which optional capabilities actually started on this machine, and the only user-facing
report for the five that used to degrade silently.

### Optional-capability pattern

The rule for anything optional: **probe, report, degrade — never throw at startup.** Three live
instances, and new optional dependencies should take the same shape:

- **The cutout model.** `u2netp.onnx` is absent from a fresh clone. The WinUI csproj includes it
  only `Condition="Exists(...)"`; when missing, `EnginePicker` drops `OnnxU2Net`,
  `CutoutService.IsAvailable` goes false, the Cutout page shows a banner, and the service returns
  `EngineUnavailable` naming the download script. Cutout tests skip themselves.
- **Face detection.** [IFaceLocator.cs](src/PoChopAudio.Services/Cutout/IFaceLocator.cs) is the
  interesting variant: the interface lives in `Services` with **no implementation there**, because
  the only implementation is a Windows API and `Services` may not reference an OS framework. The app
  registers [WindowsFaceLocator.cs](src/PoChopAudio.WinUI/Services/WindowsFaceLocator.cs), and
  `CutoutService` takes it as an optional constructor parameter defaulting to null. A measured chin
  replaces `HeadFinder`'s inference of where the neck is when it is available; the mask-shape logic
  runs unchanged when it is not, which is why the tests never need it.
- **Platform codecs.** M4A/AAC/WMA go through `MediaFoundationReader`, Windows-only.
  `AudioDecoder.IsSupportedExtension` reports the list for the running platform, so the UI never
  offers a format that cannot be read.

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
| `tests/PoChopAudio.Unit` | Pure logic — detector, exporters, decoder, edge processor, head finder, job store | 105 tests, all passing. Add here first |
| `tests/PoChopAudio.Integration` | Multi-component behaviour against the real services | 20 tests, all passing |

Both reference `Services` and `Shared` directly. `Integration` used to drive the HTTP pipeline
through `WebApplicationFactory`; when the API was deleted its export-maths and cutout-pipeline tests
were ported to call the services instead, which is closer to what the app actually does. It links in
`u2netp.onnx` from the WinUI project `Condition="Exists(...)"`, so its cutout tests skip on a fresh
clone rather than fail.

The camera, the microphone, the face locator and the WinUI UI itself need real hardware and a real
window, and are **not covered by any automated test** — the log file is the only way to see them
work.
