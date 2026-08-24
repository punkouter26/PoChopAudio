# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Governance documents

Three docs already exist and are authoritative — read them before making structural changes:

- [NET_RULES.md](NET_RULES.md) — the standing rules for this codebase (naming, layout, API/security,
  Blazor UI, observability, testing). Non-negotiable unless the user says otherwise.
- [AGENT.md](AGENT.md) — the living architecture doc. It records **what was actually built** and why,
  including a "Deliberately absent" section (no auth, no primary DB, no paid AI services). Update it
  when architecture changes; do not re-add things it explicitly rejects without asking.
- [README.md](README.md) — user-facing behaviour of the chop knobs.

## Commands

```powershell
# Restore + build (Release) + run all tests. Add -Run to publish and start on http://localhost:5177
./SCRIPTS/setup.ps1
./SCRIPTS/setup.ps1 -Run

# Fetch the ~4.4 MB u2netp.onnx cutout model into src/PoChopAudio.API/Content/Models (idempotent)
./SCRIPTS/download-models.ps1

# Assert the browser recorder's WAV header still matches what NAudio expects (node only, no browser)
node SCRIPTS/verify-recorder-wav.js

# Assert the head-shot crop bounds and store-only ZIP layout are still correct (node only)
node SCRIPTS/verify-camera.js

# Post-build smoke test: /health, /diag, and the Blazor #app mount point
./SCRIPTS/smoke-test.ps1 -Url 'http://localhost:5177'
```

Plain dotnet, against the `.slnx` solution:

```powershell
dotnet build PoChopAudio.slnx -c Release
dotnet test PoChopAudio.slnx                              # all four test projects
dotnet test tests/PoChopAudio.Unit                        # one project
dotnet test tests/PoChopAudio.Unit --filter "FullyQualifiedName~SegmentDetectorTests"
dotnet test tests/PoChopAudio.Unit --filter "DisplayName~Sweeps"   # one test
```

`TreatWarningsAsErrors` is on solution-wide — a warning fails the build.

### Do not use `dotnet run` to see the app

`dotnet run` on the API does not copy the Client's static web assets (`index.html`, `_framework/`)
into the API's bin, so the browser gets an unfilled placeholder page. `setup.ps1 -Run` publishes
first and then patches two known Blazor-SDK asset gaps (resolved HTML placeholders and fingerprinted
`_framework` files). If you change how the client is hosted, that script is where the workaround lives.
`launchSettings.json` still uses port 5294; the supported entry point is 5177 via the script.

## Architecture in one pass

.NET 10 / C# 15. Three source projects, four test projects, vertical slices.

- **`src/PoChopAudio.API`** — Minimal API host that also serves the Blazor WASM client
  (`UseBlazorFrameworkFiles` + `MapFallbackToFile("index.html")`). Everything is wired in
  [Program.cs](src/PoChopAudio.API/Program.cs); each feature exposes one `Map{Feature}Endpoints`
  extension over `MapGroup()`.
- **`src/PoChopAudio.Client`** — Blazor WASM. Scoped `.razor.css` only, no inline styles.
- **`src/PoChopAudio.Shared`** — contracts referenced by both sides. Limits (`ChopLimits`,
  `CutoutLimits`) are `const` here so the UI can never offer what the API will reject; keep the
  two ends in sync by editing only this project.

Two independent features, same shape:

| | Chop (audio) | Cutout (image) |
| --- | --- | --- |
| Slice | `Features/Chop` | `Features/Cutout` |
| Routes | `/api/chop/*` | `/api/cutout/*` |
| Job store | `ChopJobStore` | `CutoutJobStore` |
| Temp dir | `%TEMP%/PoChopAudio` | `%TEMP%/PoChopAudioCutout` |

Both follow **decode once, keep bytes on disk, keep metadata in memory**: upload decodes to a
canonical form in a temp job directory, analyze re-runs only the cheap tuning step against it, and
download slices/encodes on demand. Jobs expire after 2 h and the directory is wiped on start and
shutdown — there is no persistent store of user media. Per NET_RULES §2, slices never reference each
other; anything two slices need goes in `Shared`.

`Features/Archive` + `Storage/AzuriteBlobStore` persist only a **best-effort** batch recency index to
Azurite. Every write is caught and logged, so a missing Azurite degrades the "Recent batches" list and
nothing else. Never make a code path depend on Azurite being reachable.

### Where the real logic is

The interesting, test-covered parts are pure and I/O-free — put new logic there, not in endpoints:

- [SegmentDetector.cs](src/PoChopAudio.API/Features/Chop/SegmentDetector.cs) — the split algorithm.
  It does not pick a fixed threshold: it sweeps every gate from `noiseFloor + 3 dB` to `peak - 3 dB`
  in 0.5 dB steps and keeps the centre of the *widest band of gates that agree on the expected
  count*. Read the Detection section of AGENT.md before touching it.
- [ClipExporter.cs](src/PoChopAudio.API/Features/Chop/ClipExporter.cs) — WAV slicing and
  `UniqueStems`, the case-insensitive de-duplication that makes the flat batch ZIP safe on Windows.
  Also the two-pass export: normalization needs the whole clip measured before the first sample can
  be written, so it reads the slice twice rather than buffering it.
- [ClipProcessor.cs](src/PoChopAudio.API/Features/Chop/ClipProcessor.cs) — export gain and fade
  maths, no I/O. The three guards (silence, ceiling, max gain) live in `DecideGain`.
- [LoudnessMeter.cs](src/PoChopAudio.API/Features/Chop/LoudnessMeter.cs) — ITU-R BS.1770-4. The
  K-weighting biquads come from the analog prototype, **not** the RBJ cookbook shelf, which is a
  different filter and reads ~0.25 LU low. Read the Export polish section of AGENT.md before
  touching it.
- [EdgeProcessor.cs](src/PoChopAudio.API/Features/Cutout/EdgeProcessor.cs) — mask threshold,
  morphology, feather, alpha multiplier.
- [ImageDecoder.cs](src/PoChopAudio.API/Features/Cutout/ImageDecoder.cs) — decode, EXIF auto-rotate,
  Pixel Motion Photo (`.MP.jpg`) trailer strip.

### Browser recording

`RecorderPanel.razor` → `AudioRecorder.cs` → `wwwroot/js/recorder.js` captures from the microphone
and **writes its own 16-bit PCM WAV in the browser**. Do not swap this for `MediaRecorder`: it emits
WebM/Opus on Chrome/Firefox and MP4/AAC on Safari, and `AudioDecoder` handles neither off Windows,
so it would work locally and fail for most users. Writing the WAV client-side means a recording is
byte-for-byte an ordinary upload from `PostUploadAsync` onward.

The WAV header is the one place the browser and NAudio must agree, and a mistake there surfaces as
an opaque 422. `node SCRIPTS/verify-recorder-wav.js` loads the real `recorder.js` and asserts every
field — run it after touching the encoder. Mic capture itself (getUserMedia, AudioWorklet, the meter)
needs a real browser and microphone and is not covered by any automated test.

### Head shots are deliberately server-free

`/headshots` ([HeadShots.razor](src/PoChopAudio.Client/Pages/HeadShots.razor) →
[CameraCapture.cs](src/PoChopAudio.Client/Services/CameraCapture.cs) →
`wwwroot/js/camera.js`) captures, cuts out, crops and zips entirely in the browser. **Do not
"simplify" it onto the Cutout page's pipeline** — that uploads before it analyses, and the whole
reason this page exists separately is that photographs of a user's face should not leave the
machine. Pixels stay in a JS-side map; C# only ever holds ids and object URLs.

Cropping uses the alpha bounding box (`subjectBounds`), not face detection — after masking, the
only opaque pixels are the head. `node SCRIPTS/verify-camera.js` covers the crop bounds and the
store-only ZIP layout. The camera itself needs a real browser and is not covered by any test.

**Known defect, not caused by this page:** `CutoutEngine.BrowserOnnx` on the Cutout page silently
does nothing. `ReanalyzeOneAsync` computes the mask, discards it, and reports success, while the UI
and ZIP fetch the server's never-cut copy. There is no endpoint to post a browser result back.

### Optional-capability pattern

Two things are absent from a fresh clone and the code must keep working without them:

- **`u2netp.onnx`** — the API csproj includes it only `Condition="Exists(...)"`. When missing,
  `EnginePicker` drops `OnnxU2Net` from `/api/cutout/capabilities` and the UI hides it; the
  browser-side ONNX engine still works. Cutout E2E tests skip themselves when the model is absent.
- **Platform codecs** — M4A/AAC/WMA go through `MediaFoundationReader` and are Windows-only.
  `AudioDecoder.IsSupportedExtension` and `/diag` report the list for the running platform.

Follow this pattern for anything else optional: probe, report through `/capabilities` or `/diag`,
degrade — never throw at startup.

## Conventions worth knowing

- **IDs are `readonly record struct`** with `TryParse` (`JobId`, `CutoutJobId`) — no bare `Guid` or
  `string` ids crossing a boundary. Closed sets are enums (`CutoutEngine`).
- **Logging** goes through `[LoggerMessage]` source generators (`ChopLog`, `CutoutLog`). No string
  interpolation in log calls.
- **Endpoints return `TypedResults`** with an explicit `Results<...>` union; failures are
  `TypedResults.Problem` with a real status code, never an exception escaping to a 500.
- **Batch failure is per file.** A file that will not decode, or that finds the wrong take count, is
  flagged and still included in the ZIP; the batch never aborts. Unknown/expired job ids in a batch
  request are skipped, and only an entirely unusable set is a 404.
- **Per-file settings pin.** Touching a file's own knobs sets `UsesOwnSettings`, excluding it from
  *Re-split all*. Preserve that when changing batch behaviour.
- **Packages are pinned centrally** in [Directory.Packages.props](Directory.Packages.props); project
  files carry bare `<PackageReference Include="..." />` with no version.
- NET_RULES §1 says trunk-based on `master`; this repo's trunk is actually `main`.

## Test layout

| Project | Scope | State |
| --- | --- | --- |
| `tests/PoChopAudio.Unit` | Pure logic, no I/O — detector, exporters, decoder, edge processor | Real coverage; add here first |
| `tests/PoChopAudio.Integration` | `WebApplicationFactory<Program>` HTTP pipeline | `CutoutPipelineTests` and `ClipExportTests` real; `IntegrationTests` still a placeholder |
| `tests/PoChopAudio.E2EAPI` | Contract constants + cutout against real sample photos | **4 tests fail in a fresh clone** — `CutoutSamplePhotosTests` needs `PXL_*.jpg` files in the repo root that were never committed. It guards on the missing ONNX model but not on missing photos. |
| `tests/PoChopAudio.E2EUI` | Playwright, mobile + desktop per NET_RULES §6 | **Placeholder only** — no Playwright dependency yet |

NET_RULES §6 coverage targets: Unit 100 %, Integration 50 %, API E2E 25 %, UI E2E 25 %.
