# PoChopAudio — architecture

Recordings in, one WAV per take out. Point it at files holding five repetitions of a sound
separated by pauses and it returns five clips each, trimmed to the start and end of every take.

Governance lives in [NET_RULES.md](NET_RULES.md); this file records what was actually built.

## Shape

```
src/PoChopAudio.API/          Minimal API + BFF host; serves the Blazor client
  Features/Chop/              The whole splitting feature (endpoints, DTO wiring, DSP, storage)
  Features/Cutout/            Image background-removal feature
    ImageDecoder              JPEG/PNG/WebP decode, EXIF auto-rotate, MP.jpg trailer strip
    EdgeProcessor             Mask threshold, morphology, feather, alpha multiplier
    Engines/OnnxU2NetRemover  u2netp ONNX model, in-process
    CutoutJobStore            Same temp-dir / 2 h TTL pattern as ChopJobStore
    CutoutEndpoints           /api/cutout/{upload, capabilities, analyze, image, images.zip}
  Features/Diagnostics/       /health and /diag
  Features/Archive/           Batch metadata persisted to Azurite (best-effort, non-blocking)
    AzuriteBlobStore           Thin wrapper over the blob container; swallows errors when
                                Azurite is unreachable so the in-memory job store keeps working
    JobArchive                 Batch JSON at batches/{id}.json + a capped recency index
    ArchiveEndpoints            /api/archive/batches {GET list, GET one, POST save, DELETE}
src/PoChopAudio.Client/       Blazor WASM UI
  Components/ChopFileCard.razor   One recording
  Components/ChopKnobs.razor      The five chop settings
  Components/CutoutFileCard.razor One image
  Components/CutoutKnobs.razor    Alpha threshold / feather / morphology / multiplier
  Pages/Home.razor           The chop page
  Pages/Cutout.razor         The cutout page (per-image engine picker, checkerboard preview)
  Services/CutoutClient.cs   Typed HttpClient for /api/cutout
  Services/BrowserOnnxRemover Client-side ONNX runtime (when no server engine is available)
  wwwroot/js/cutout.js       onnxruntime-web + u2netp in the browser via JS interop
src/PoChopAudio.Shared/       Contracts shared by both: JobId, ChopLimits, ChopOptions, results
                              plus CutoutLimits, CutoutEngine, IBackgroundRemover, CutoutOptions
tests/PoChopAudio.Unit/       SegmentDetector, ClipExporter, ImageDecoder, EdgeProcessor, CutoutExporter
tests/PoChopAudio.Integration/  Full HTTP pipeline with WebApplicationFactory<Program>
output/                       Clips produced from the Matt_*.m4a recordings
```

## How a file becomes clips

1. **Upload** — `POST /api/chop/upload`. The file is written to a temp job directory and decoded
   once into `canonical.wav` (32-bit float, source rate and channel count). Nothing is decoded twice.
2. **Envelope** — the same decode pass builds a 10 ms-per-frame RMS loudness trace plus a 900-point
   peak trace for the waveform view. Both live in memory on the job; the audio lives on disk.
3. **Detect** — `POST /api/chop/{jobId}/analyze`. See below.
4. **Export** — `GET /api/chop/{jobId}/clips/{index}` or `clips.zip` slices `canonical.wav` and
   writes 16-bit PCM WAV.

Jobs are scratch space: they live under `%TEMP%/PoChopAudio`, expire after two hours, and the
directory is wiped on startup and shutdown.

## Batches

A job is still exactly one recording. A batch is nothing more than the set of jobs the client is
currently holding, which is why there is no batch entity, no second lifetime to manage, and no way
for a batch to outlive the audio it points at.

- **Upload** is sequential, one file at a time. Each file is buffered whole in the browser before it
  is posted, so uploading several 250 MB files at once would only trade throughput for memory.
- **Settings** are shared until a file's own knobs are touched. Touching them sets `UsesOwnSettings`,
  which excludes that file from *Re-split all* — one badly-recorded take never forces the rest to be
  re-tuned, and re-tuning the rest never silently discards the fix.
- **Failure is per file.** A file that will not decode, or that finds the wrong number of takes, is
  flagged and left in place; the others finish and the flagged file is still exported.
- **Export** is `GET /api/chop/clips.zip?jobs=…&jobs=…`, capped at `ChopLimits.MaxBatchFiles`. Unknown
  or expired ids are skipped rather than fatal, so one lapsed job cannot block the download; only an
  entirely unusable set returns 404.
- **Names** stay flat — `Nick_Happy_1.wav … Nick_Sad_5.wav` — because the source name is already the
  prefix. `ClipExporter.UniqueStems` guarantees the flatness is safe: two uploads called
  `Nick_Happy` become `Nick_Happy` and `Nick_Happy(2)`, compared case-insensitively so a Windows
  extraction cannot overwrite a clip.

## Detection

`SegmentDetector` never guesses a fixed threshold. It sweeps every gate from `noiseFloor + 3 dB` to
`peak - 3 dB` in 0.5 dB steps, counts how many takes each gate produces, and keeps the gate at the
centre of the **widest band of gates that all agree on the expected count**. A wide band means the
answer does not depend on the exact number, which is what a clean split looks like.

Then:

- Quiet stretches shorter than `MinGapMs` do not split a take in two.
- Runs shorter than `MinSegmentMs` are dropped — this is what removes marker pulses between takes.
- More runs than expected: the longest N win. Fewer: the caller gets a warning, never a silent lie.
- Each take's boundaries are followed outward while the signal stays within 9 dB of the gate, so the
  attack and decay are inside the clip, then padded by `PadMs` and clamped to the midpoint of the
  gap so neighbouring clips can never overlap.
- Peak-to-floor contrast under 6 dB means silence or an unbroken wall of sound: zero clips, plus a
  warning saying so.

`ChopOptions` is the only tuning surface, and every knob is exposed in the UI.

## Decoding

| Format | Reader | Platform |
| --- | --- | --- |
| WAV | `WaveFileReader` | any |
| MP3 | `Mp3FileReaderBase` + NLayer | any (managed decoder) |
| AIFF | `AiffFileReader` | any |
| M4A, AAC, WMA, MP4 | `MediaFoundationReader` | Windows only |

`AudioDecoder.IsSupportedExtension` and `/diag` both report the list for the running platform, so the
UI never offers a format the server cannot read.

## Cutout

`/api/cutout` takes an image and returns a PNG with the background removed. Two engines, both
free, picked per batch via the API picker:

| Engine | Where it runs | Quality | Cost |
| --- | --- | --- | --- |
| `OnnxU2Net` | Server (Microsoft.ML.OnnxRuntime + u2netp.onnx) | Good for portraits | Free |
| `BrowserOnnx` | Browser (onnxruntime-web + u2netp.onnx via JS interop) | Same | Free, no upload of raw pixels |

`IBackgroundRemover` lives in `PoChopAudio.Shared`; the API registers one implementation per
engine, and the `EnginePicker` exposes only the available ones through `/api/cutout/capabilities`.
The UI then offers exactly that picker.

The pipeline is decode-once, process-once, encode-once:

1. **Upload** — `POST /api/cutout/upload`. The image is decoded once into raw RGBA bytes
   (`ImageSharp`, EXIF auto-rotate, Pixel Motion Photo `.MP.jpg` trailer stripped). The original
   file is discarded; the working copy lives in `%TEMP%/PoChopAudioCutout` and is wiped on
   shutdown.
2. **Analyze** — `POST /api/cutout/{jobId}/analyze`. The picker selects an engine; the engine
   returns the alpha mask as RGBA bytes. `EdgeProcessor` applies the four user knobs (threshold,
   morphology, feather, multiplier) and the background fill, then `ImageDecoder.EncodePng` writes
   the final PNG. The processed RGBA replaces the originals in the job so subsequent downloads
   are fast.
3. **Download** — `GET /api/cutout/{jobId}/image` for one image, `GET /api/cutout/images.zip?jobs=…`
   for a batch. ZIP filenames are flat (`<stem>_cutout.png`, with `(N)` suffix for collisions)
   so the archive reads like the output folder.

Per-file knobs mirror the chopper: alpha threshold 0-255, feather 0-5 px, morphology -3 to +3 px,
optional background fill. Defaults are tuned for portraits; an image that needs no tweak usually
ships with the defaults.

The model file (`u2netp.onnx`, ~4.4 MB) lives in `src/PoChopAudio.API/Content/Models/`. The
`<None Include="Content/Models/u2netp.onnx" Condition="Exists(...)">` clause means a fresh clone
builds without it — the API then reports `OnnxU2Net` as unavailable and the UI hides it.
`SCRIPTS/download-models.ps1` fetches the model from the public U-2-Net release into the right
folder. It is licensed Apache-2.0; the license file is downloaded alongside.

The browser engine downloads `onnxruntime-web` from jsDelivr at first use, caches the model in
IndexedDB, and posts the masked PNG back as `image/png`. The model file is served from the API
host at `_content/cutout-models/u2netp.onnx`.

## Deliberately absent

- **No auth.** Nothing is protected, so there is no BFF cookie flow, no Entra ID, no
  `FakeAuthHandler`. Add `Features/Auth` and a `FallbackPolicy` if this is ever hosted for more than
  one person — the NET_RULES rules for that path still apply.
- **No primary database.** A job is still a temp directory that resets on restart. Azurite
  (`Features/Archive`) only holds a lightweight, best-effort recency index of past batches
  (used by the Cutout page's "Recent batches" list) — never the audio or image bytes
  themselves, which are gone once the temp directory is wiped. It is not required to run
  the app: every write is caught and logged, so a missing Azurite degrades archive listing,
  not the chop/cutout pipelines.
- **No paid AI services.** Both engines are free (on-device ONNX models). remove.bg was considered
  and dropped per the no-paid-services decision.
- **No per-file progress bar.** Upload is sequential and the status line names the file it is on,
  which is enough at this scale; a real progress bar needs a streaming upload the API does not offer.
