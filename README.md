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

**Batch settings** at the top apply to every file, and **Re-split all** re-runs them. Turning a knob
*inside* a file re-splits that file immediately and pins it: from then on it ignores *Re-split all*,
so fixing one awkward recording never disturbs the others. **Follow batch settings** un-pins it.

A file that fails to decode, or that finds a different number of takes than you asked for, is
flagged rather than fatal — the rest of the batch still finishes, and the flagged file is still
included in the ZIP.

Supported input: WAV, MP3, AIFF everywhere; M4A, AAC, WMA additionally on Windows. Output is always
16-bit PCM WAV at the source sample rate and channel count.

## Layout

`src/PoChopAudio.API` (host + splitting), `src/PoChopAudio.Client` (Blazor WASM UI),
`src/PoChopAudio.Shared` (contracts), `tests/PoChopAudio.Unit`. See [AGENT.md](AGENT.md) for how
detection works.
