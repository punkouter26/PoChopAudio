# PoChopAudio

Takes recordings that each contain several takes of the same sound and splits them into one WAV per
take, trimmed to the start and end of the sound.

## Run it

```powershell
./SCRIPTS/setup.ps1 -Run
```

Then open <http://localhost:5177>. Pick one file or a whole folder's worth — up to 32 at a time —
and each is split on its own.

API docs (Development only): <http://localhost:5177/scalar/v1>

## Using it

Every file gets a row showing how many takes it found. Open a row for its waveform, inline players,
per-clip downloads, and its own copy of the settings. **Download all as ZIP** gives you one flat
archive of every clip in the batch, named after its source: `Nick_Happy_1.wav … Nick_Sad_5.wav`.

Defaults assume five takes, each at least 150 ms long, separated by at least 250 ms of quiet, with
40 ms of padding kept either side. If a split is wrong, four knobs fix it:

| Knob | Turn it when |
| --- | --- |
| Takes expected | The recording has a different number of takes |
| Shortest take | A click or marker pulse is being counted as a take (raise it) |
| Shortest gap | One take is being cut in two (raise it), or two takes are merging (lower it) |
| Padding | Clips feel clipped at the edges (raise it) |

Leave **Threshold** on *auto* unless the recording is noisy — auto picks the loudness gate that the
recording most clearly supports. Turning auto off exposes the slider.

### Recording

You can record straight into the app instead of picking a file. Type a name, hit **Record**, wait
out the 3-2-1 count-in, and perform every take in one pass with a pause between them — it is split
exactly like an uploaded file.

The name is what the clips are called: `Nick_Happy` gives you `Nick_Happy_1.wav … Nick_Happy_5.wav`.
Leave it blank and you get a timestamp instead. Record a second time under a name already in the
batch and it becomes `Nick_Happy(2)`, so nothing is overwritten.

While recording you get a level meter, a scrolling waveform, and the elapsed time. If the meter
turns red the input is clipping — turn the mic gain down and go again, because clipped samples
cannot be recovered afterwards. Recording stops itself at about 43 minutes, which is where one
upload stops fitting.

Recordings join the same batch as uploaded files, so they share the batch settings, the export
knobs, and the combined ZIP. Nothing is uploaded until you stop.

Two caveats: the browser will ask for microphone permission the first time, and recording needs a
secure connection — `localhost` counts, plain HTTP over a network does not. Where it is unavailable
the app says so and the file picker still works.

### Export

Under the batch settings, **Export** decides what happens to a clip on its way out. Everything
defaults to off, so by default a clip is exactly the samples that were cut.

| Knob | What it does |
| --- | --- |
| Normalize | *Off*, *Peak* (match the loudest sample), *RMS* (match average power), or *Loudness* (match perceived level in LUFS) |
| Target | The level to hit, in dBFS or LUFS depending on the mode |
| Ceiling | Hard limit on the loudest sample. Gain is pulled back rather than allowed to clip, so a clip can land quieter than the target |
| Fade in / out | A short fade at each edge, in ms. 5 ms is enough to kill an edge click |

*Loudness* is the closest match to how loud a take actually sounds. Note that a mono clip measures
about 3 LU below the same audio in stereo, so mono takes receive that much more gain — that is what
the loudness standard specifies. On takes shorter than 400 ms use *RMS*; loudness gating has no
complete block to work with down there.

Export settings apply to the whole batch and never change the split, so turning one is instant —
no file is re-analysed. Very quiet clips are left alone rather than amplified, and gain is capped
at +24 dB so a take that is mostly room tone cannot be inflated into a wall of hiss.

**Batch settings** at the top apply to every file, and **Re-split all** re-runs them. Turning a knob
*inside* a file re-splits that file immediately and pins it: from then on it ignores *Re-split all*,
so fixing one awkward recording never disturbs the others. **Follow batch settings** un-pins it.

A file that fails to decode, or that finds a different number of takes than you asked for, is
flagged rather than fatal — the rest of the batch still finishes, and the flagged file is still
included in the ZIP.

Supported input: WAV, MP3, AIFF everywhere; M4A, AAC, WMA additionally on Windows. Output is always
16-bit PCM WAV at the source sample rate and channel count.

## Head shots

**Head shots** in the nav takes portraits with your camera and cuts each one out automatically.

Type a name, pick burst or manual, and shoot. Burst mode counts down between frames so you have
time to turn your head for the next angle; manual takes one per press. An oval guide helps you line
up. Each shot has its background removed and is cropped tight to your head as soon as it is taken,
and you can re-cut, delete, or retake any of them — the numbering stays contiguous.

A name of `Head` saves `Head_1.png, Head_2.png, …`, either one at a time or as a single ZIP.

**Nothing is uploaded.** The camera, the background-removal model, the crop and the ZIP all run in
the browser tab; no photograph of your face reaches the server or the disk unless you save it. The
first cutout downloads the ONNX runtime, so it needs the network once. Like recording, the camera
needs a secure connection — `localhost` counts, plain HTTP does not.

## Layout

`src/PoChopAudio.API` (host + splitting), `src/PoChopAudio.Client` (Blazor WASM UI),
`src/PoChopAudio.Shared` (contracts), `tests/PoChopAudio.Unit`. See [AGENT.md](AGENT.md) for how
detection works.
