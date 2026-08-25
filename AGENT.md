# PoChopAudio — architecture

Recordings in, one WAV per take out. Point it at files holding five repetitions of a sound
separated by pauses and it returns five clips each, trimmed to the start and end of every take.

Governance lives in [NET_RULES.md](NET_RULES.md); this file records what was actually built.

> **PoChopAudio is a WinUI 3 desktop app.** The ASP.NET Minimal API (`PoChopAudio.API`) and the
> Blazor WASM client (`PoChopAudio.Client`) were deleted: everything now runs in-process on the
> user's machine and nothing is uploaded. Sections below that describe HTTP endpoints record how
> the feature *was* reached, not how it is reached today — the logic behind them survived the move
> into `PoChopAudio.Services` unchanged, which is why they are still worth reading.

## Shape

```
src/PoChopAudio.Services/     Host-agnostic engine room. No UI, no hosting framework.
  Outcome.cs                  Value-or-domain-reason return type used by both services
  ExportedFile.cs             Bytes + filename + content type, ready to save
  Chop/ChopService            The whole chop feature: upload, analyze, clip, zip, delete
  Chop/AudioDecoder           Decode to canonical WAV; MediaFoundation codecs are Windows-only
  Chop/SegmentDetector        The split algorithm (gate sweep, widest agreeing band)
  Chop/SilenceTrimmer         Leading/trailing silence trim, in frames
  Chop/ClipExporter           WAV slicing, UniqueStems, two-pass normalized export, ZIP
  Chop/LoudnessMeter          ITU-R BS.1770-4 K-weighting + gated integrated loudness
  Chop/ClipProcessor          Export maths: gain decision and fade curve, no I/O
  Chop/ChopJobStore           Temp-dir job scratch space, 2 h TTL
  Cutout/CutoutService        CutOutAsync: decode, mask, clean, head-crop, encode — one call
  Cutout/ImageDecoder         JPEG/PNG/WebP decode, EXIF auto-rotate, MP.jpg trailer strip
  Cutout/EdgeProcessor        Mask threshold, morphology, feather, alpha multiplier
  Cutout/HeadFinder           Crops to the head alone: peak, neck, shoulder flare
  Cutout/Engines/OnnxU2NetRemover  u2netp ONNX model, in-process
  Cutout/CutoutModelOptions   Injected path to u2netp.onnx — why the engine needs no host
src/PoChopAudio.Shared/       Contracts: JobId, ChopLimits, ChopOptions, ExportOptions, results,
                              CutoutLimits, CutoutEngine, IBackgroundRemover, CutoutOptions
src/PoChopAudio.WinUI/        The app. Unpackaged, self-contained Windows App SDK.
  App.xaml.cs                 DI registration — the same singletons the API used to register
  MainWindow.xaml             NavigationView shell: Chop Audio (excluded from build), Cutout Studio
  Views/CutoutPage            Camera viewfinder, TAKE PHOTO, and the cut-out results below it
  Services/CameraService      MediaFrameReader viewfinder + still capture
  Services/AudioRecorderService, AudioPlayerService, ExportService
  Services/ServiceOutcomeExtensions  .OrThrow(), the Outcome-to-exception seam
  Controls/WrapPanel          WinUI 3 ships none; the knob row needed one
  Content/Models/u2netp.onnx  Shipped with the app (optional; absent = cutout unavailable)
tests/PoChopAudio.Unit/       Pure logic: detector, exporters, decoder, edge processor
tests/PoChopAudio.Integration/  Multi-component behaviour against the real services
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

`ChopOptions` is the only tuning surface for detection, and every knob is exposed in the UI.

## Recording

The Chop page can capture from the microphone instead of taking a file. A recording joins the same
batch as an upload and is split by the same detector — there is no recording entity, no second
lifetime, and no separate page, because after the first few milliseconds there is nothing to tell a
recording apart from an uploaded `.wav`.

**The browser writes the WAV itself.** `MediaRecorder` would be less code, but it emits WebM/Opus on
Chrome and Firefox and MP4/AAC on Safari, and `AudioDecoder` handles neither off Windows — so the
obvious implementation produces a feature that works on the author's machine and fails on everyone
else's. Instead an `AudioWorklet` captures float samples and `recorder.js` writes a canonical 44-byte
RIFF header around 16-bit PCM. The upload then travels the identical path as a picked file: same
decode, same detection, same clip naming, no server-side codec that exists for this one feature.

- **Capture is deliberately unprocessed.** `echoCancellation`, `autoGainControl` and
  `noiseSuppression` are all off. AGC in particular would ride the level between takes and destroy
  exactly the consistency the detector measures; noise suppression would eat the quiet gaps it
  splits on.
- **The worklet batches.** Audio arrives in 128-frame quanta — 375 a second at 48 kHz — so frames
  are gathered into ~85 ms blocks before crossing to the main thread, and the partial final block is
  flushed on stop so the tail of a take is not dropped.
- **Length is capped, not silently truncated.** Uncompressed mono at 48 kHz is 5.76 MB/min, so
  capture auto-stops just under `ChopLimits.MaxUploadBytes` (~43 min) and says why.
- **Naming is the whole point.** A recording has no filename to take a stem from, so the panel has a
  name field: `Nick_Happy` becomes `Nick_Happy_1.wav … Nick_Happy_5.wav` through the existing
  `ClipExporter.ClipFileName`. The page de-duplicates against the batch up front rather than leaving
  `UniqueStems` to resolve it silently at download time.

`getUserMedia` needs a secure context. `localhost` qualifies, so development is unaffected, but
serving this over plain HTTP disables recording — `AudioRecorder.IsSupportedAsync` detects that and
the panel explains it instead of failing at the click.

The header is where the browser and NAudio have to agree, and a wrong field surfaces as an opaque
422 rather than anything diagnosable. `SCRIPTS/verify-recorder-wav.js` loads the real `recorder.js`
in a node stub and asserts every field; it needs no browser and no microphone.

## Head shots

`/headshots` takes a set of portraits from the camera and cuts each one out automatically. It is
the one feature that does **not** touch the API, and that is the whole point: photographs of
someone's face are the input where "we only upload it in order to process it" is the wrong trade.
Capture, background removal, cropping, preview and the ZIP all happen in the tab, and the only
bytes that ever leave are the ones the user saves to disk.

That means it deliberately does not reuse the Cutout page's pipeline, which uploads before it
analyses. Two consequences worth knowing:

- **There is no job, no `CutoutJobId`, and no temp directory.** Shots live in a JS-side map keyed
  by id; C# holds ids and object URLs and never the pixels, so a page of 2 MP photographs costs the
  .NET heap almost nothing.
- **The ZIP is written in the browser**, store-only, by about eighty lines in `camera.js`. PNGs are
  already deflated so compression would buy nothing, and pulling a ZIP library over the network
  would undo the self-containment the feature exists for. General-purpose bit 11 is set so a name
  the user typed with non-ASCII characters survives extraction.

**Cropping needs no face detection.** Once the mask is applied the only opaque pixels left *are* the
head, so `subjectBounds` takes the alpha bounding box, pads it, and clamps to the frame. An alpha
floor of 8 rather than 0 keeps feathered edges from dragging the box out to the whole picture. The
oval on the preview is advisory only — nothing enforces it.

The preview is mirrored in CSS so lining a shot up feels like a mirror; the captured frame is
deliberately *not* flipped, because saving the mirror would hand back a reversed photograph.

`getUserMedia` needs a secure context, same as the recorder. `SCRIPTS/verify-camera.js` checks the
two pieces nothing else validates — the crop bounds and the ZIP byte layout — with no browser
required.

### Known defect on the Cutout page

`CutoutEngine.BrowserOnnx` does not work and reports success anyway. `Cutout.razor`'s
`ReanalyzeOneAsync` computes the masked PNG in the browser, discards it, and sets `file.Result`;
the card and the batch ZIP then both fetch `api/cutout/{jobId}/image`, which is the server's copy
that no engine ever touched. There is no endpoint to post a browser-side result back — the claim
elsewhere in this document that the browser engine "posts the masked PNG back" describes an
intention, not the code. Fixing it needs either a `PUT /api/cutout/{jobId}/image` or the local-only
approach `/headshots` took.

## Export polish

A clip is a plain slice of `canonical.wav` unless the download URL asks for more. `ExportOptions`
carries four knobs — normalize mode, target, ceiling, and the two fades — and every one of them
defaults to doing nothing, so a bare download URL still returns exactly the samples that were cut.

**They live on the URL, not in `ChopOptions`.** Nothing about a fade changes where the takes are, so
folding them into detection would re-run the whole analysis for an answer that cannot have changed.
Riding on the query string also means the URL changes when the settings do, which is what stops the
browser replaying a clip cached under the previous settings.

| Mode | Measures | Use it when |
| --- | --- | --- |
| `None` | nothing | The default. Bit-for-bit the raw cut. |
| `Peak` | loudest sample | Predictable, but one stray click keeps a quiet take quiet. |
| `Rms` | mean square | Steadier across takes, and reliable below 400 ms where gating cannot run. |
| `Lufs` | ITU-R BS.1770-4 integrated | Closest to perceived loudness. |

Three guards decide the gain, in order, and each is reported on `ClipGain` so the reason is never
invisible:

1. **Silence** — below `ExportLimits.SilenceFloorDb` (-70) the clip is left alone. Normalizing
   digital silence is a divide by zero wearing a hat.
2. **Ceiling** — if the target would push the loudest sample past `CeilingDb`, the ceiling wins and
   the clip lands quieter than asked. A loudness target is never met by clipping.
3. **Max gain** — `ExportLimits.MaxGainDb` (+24) caps the rest, so targeting -16 LUFS on a take that
   is mostly room tone cannot raise the noise by 40 dB and call it a performance.

Fades are raised-cosine rather than linear, so a 5 ms edge treatment removes a click without leaving
a corner of its own, and two fades longer than the clip are scaled to fit instead of multiplying
into silence in the middle.

### Two things worth knowing about the meter

`LoudnessMeter` builds the K-weighting biquads by bilinear-transforming the analog prototype, which
reproduces the BS.1770-4 coefficient table exactly at 48 kHz **and** stays correct at every other
rate — clips keep their source rate, so a 48 kHz-only table would quietly mis-measure 44.1 kHz work.
The RBJ cookbook shelf is a different filter and reads about 0.25 LU low; the tests pin this by
asserting a dual-mono 1 kHz sine reads its own dBFS value, which is the property the -0.691 offset
exists to arrange.

Mono is measured as one channel at weight 1.0, per the standard. That makes a mono clip read about
3 LU below the same audio duplicated to stereo, so **a mono take normalized to -16 LUFS receives
3 dB more gain than a stereo one**. This matches ffmpeg and pyloudnorm. The UI says so next to the
mode picker.

Normalization needs the whole clip measured before the first sample can be written, so an export
that asks for it reads the slice twice rather than buffering it — memory stays flat whether the take
is 200 ms or the entire recording. A pass-through export skips the measuring pass entirely.

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

`IBackgroundRemover` lives in `PoChopAudio.Shared`; the host registers one implementation per
engine, and the `EnginePicker` exposes only the available ones through `/api/cutout/capabilities`.
The UI then offers exactly that picker.

`OnnxU2NetRemover` takes its model path from an injected `CutoutModelOptions` rather than reading
`IHostEnvironment.ContentRootPath`. That is what lets the engine live in `PoChopAudio.Services`
with no host dependency: the API resolves the path under its content root, and a desktop host
resolves it next to the executable. A missing file still leaves the engine simply unavailable.

`CutoutService.CutOutAsync` does the whole thing in one call:

1. **Decode** — raw RGBA via `ImageSharp`, EXIF auto-rotated, Pixel Motion Photo `.MP.jpg` trailer
   stripped.
2. **Mask** — the picker selects an engine; the engine returns the alpha mask as RGBA bytes.
3. **Clean** — `EdgeProcessor` applies threshold, morphology, feather and the alpha multiplier, in
   that order. Defaults are tuned for head shots: threshold 160 kills the soft halo u2netp leaves,
   a 1 px erode pulls the edge inside the fringe, a 1 px feather stops it looking jagged, and the
   1.6x multiplier saturates what survives so the head is solid rather than translucent.
4. **Crop** — `HeadFinder` cuts at the neck, so the result is the head and not the body.
5. **Encode** — `ImageDecoder.EncodePng` writes a transparent PNG.

This used to be four HTTP calls with a job store holding decoded pixels between them. That shape
existed because HTTP is stateless and a browser needed a handle to refer back to; in-process the
caller already holds the bytes, so `CutoutJobStore`, its 2 h expiry and its temp directory were
removed along with `CutoutExporter` (ZIP and filename templates) and `TrimHelper` (the
whole-subject crop, superseded by the head crop). `ProgressChannel` went too — it published
progress that only the deleted SSE endpoint ever subscribed to.

The model file (`u2netp.onnx`, ~4.4 MB) lives in `src/PoChopAudio.WinUI/Content/Models/`. The
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
- **No server and no web client.** The API and the Blazor client were deleted once the app became
  desktop-only. Do not reintroduce them: the logic they wrapped lives in `PoChopAudio.Services`,
  which both of them referenced anyway, and the app calls it in-process. The `Outcome<T>` return
  type is the seam that used to be HTTP status codes.
- **No primary database, and no Azurite.** A job is still a temp directory that resets on restart.
  The best-effort batch recency index went with the API; a desktop app has no Azurite to reach and
  the index was never load-bearing.
- **No paid AI services.** Both engines are free (on-device ONNX models). remove.bg was considered
  and dropped per the no-paid-services decision.
- **No per-file progress bar.** Processing is sequential and the status line names the file it is
  on, which is enough at this scale.
- **No face-detection model.** `HeadFinder` crops to the head by reading the shape of the saliency
  mask — peak, neck, shoulder flare — rather than adding a second ONNX model for faces.
