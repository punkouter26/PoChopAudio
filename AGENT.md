# PoChopAudio — architecture

Recordings in, one WAV per take out. Point it at files holding five repetitions of a sound
separated by pauses and it returns five clips each, trimmed to the start and end of every take.

Governance lives in [NET_RULES.md](NET_RULES.md); this file records what was actually built.

## Shape

```
src/PoChopAudio.API/          Minimal API + BFF host; serves the Blazor client
  Features/Chop/              The whole splitting feature (endpoints, DTO wiring, DSP, storage)
  Features/Diagnostics/       /health and /diag
src/PoChopAudio.Client/       Blazor WASM UI (single page)
  Components/                 ChopFileCard (one recording), ChopKnobs (the five settings)
  Models/                     ChopFileState / ChopSettings — per-file UI state, client only
src/PoChopAudio.Shared/       Contracts shared by both: JobId, ChopLimits, ChopOptions, results
tests/PoChopAudio.Unit/       SegmentDetector and ClipExporter naming, no I/O
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

## Deliberately absent

- **No auth.** Nothing is protected, so there is no BFF cookie flow, no Entra ID, no
  `FakeAuthHandler`. Add `Features/Auth` and a `FallbackPolicy` if this is ever hosted for more than
  one person — the NET_RULES rules for that path still apply.
- **No database.** A job is a temp directory; restarting is the reset button.
- **No integration / E2E projects yet.** The unit tests cover the detector and the clip naming,
  which is where the interesting behaviour is. Verification against real audio was done end-to-end
  by hand, including a two-file batch driven through a real browser (see `output/`).
- **No per-file progress bar.** Upload is sequential and the status line names the file it is on,
  which is enough at this scale; a real progress bar needs a streaming upload the API does not offer.
