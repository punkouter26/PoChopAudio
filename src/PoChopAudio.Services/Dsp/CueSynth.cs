namespace PoChopAudio.Services.Dsp;

/// <summary>Which cue to synthesise. Closed set, so it is an enum rather than a frequency.</summary>
public enum AudioCue
{
    /// <summary>Count-in tick. Short, dry, unpitched-sounding.</summary>
    Tick,

    /// <summary>The downbeat of a count-in, a fifth above the tick so it is distinguishable.</summary>
    Accent,

    /// <summary>Played at the start of an auditioned clip.</summary>
    ClipStart,

    /// <summary>Played at the end of an auditioned clip.</summary>
    ClipEnd,

    /// <summary>Rising two-note chime when a batch finishes.</summary>
    Success,

    /// <summary>Falling two-note chime when something failed.</summary>
    Failure,
}

/// <summary>
/// Generates the app's cue sounds as samples, with no files to ship and no assets to license.
///
/// <para>
/// Everything here is a sine with an exponential decay envelope. That is not a limitation being
/// apologised for: in an app where the user is judging recorded audio, a cue has to be immediately
/// distinguishable from the material, and a pure decaying tone is about as far from a voice or a
/// footstep as a sound can get. A sampled "ding" would be one more thing to mistake for content.
/// </para>
/// <para>
/// Pure and I/O-free by design, per the same rule as the rest of this project — the playback device
/// lives in the app, the arithmetic lives here where it can be tested.
/// </para>
/// </summary>
public static class CueSynth
{
    /// <summary>Peak amplitude of a generated cue. Deliberately quiet next to a normalised take.</summary>
    public const float DefaultAmplitude = 0.22f;

    /// <summary>
    /// Renders <paramref name="cue"/> as mono samples at <paramref name="sampleRate"/>.
    /// </summary>
    /// <param name="amplitude">Peak amplitude, 0..1. Clamped.</param>
    public static float[] Render(AudioCue cue, int sampleRate, float amplitude = DefaultAmplitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        amplitude = Math.Clamp(amplitude, 0f, 1f);

        return cue switch
        {
            AudioCue.Tick => Tone(sampleRate, amplitude, 0.045, (1000, 0.0)),
            AudioCue.Accent => Tone(sampleRate, amplitude, 0.055, (1500, 0.0)),
            AudioCue.ClipStart => Tone(sampleRate, amplitude * 0.7f, 0.05, (1320, 0.0)),
            AudioCue.ClipEnd => Tone(sampleRate, amplitude * 0.7f, 0.05, (880, 0.0)),
            AudioCue.Success => Tone(sampleRate, amplitude, 0.26, (880, 0.0), (1320, 0.09)),
            AudioCue.Failure => Tone(sampleRate, amplitude, 0.30, (660, 0.0), (440, 0.11)),
            _ => [],
        };
    }

    /// <summary>
    /// Renders a whole count-in as one buffer: <paramref name="beats"/> ticks at
    /// <paramref name="bpm"/>, the first accented, followed by one beat of silence so recording
    /// starts on the downbeat after the last tick rather than on top of it.
    ///
    /// <para>
    /// Rendered as a single buffer rather than sequenced with a timer because a count-in whose
    /// beats drift is worse than none — a timer on a busy UI thread will drift, and sample offsets
    /// cannot.
    /// </para>
    /// </summary>
    public static float[] CountIn(int beats, double bpm, int sampleRate, float amplitude = DefaultAmplitude)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(beats, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bpm, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);

        var samplesPerBeat = (int)(sampleRate * 60.0 / bpm);
        var buffer = new float[samplesPerBeat * (beats + 1)];

        for (var beat = 0; beat < beats; beat++)
        {
            var tick = Render(beat == 0 ? AudioCue.Accent : AudioCue.Tick, sampleRate, amplitude);
            var start = beat * samplesPerBeat;

            for (var i = 0; i < tick.Length && start + i < buffer.Length; i++)
            {
                buffer[start + i] += tick[i];
            }
        }

        return buffer;
    }

    /// <summary>The exact length of a count-in, so a caller can wait it out without guessing.</summary>
    public static TimeSpan CountInDuration(int beats, double bpm) =>
        TimeSpan.FromSeconds(60.0 / bpm * (beats + 1));

    /// <summary>
    /// A reference tone for setting input gain: a steady sine at a known level, so the meter can be
    /// trusted before a take rather than after one.
    /// </summary>
    /// <param name="dbfs">Level in dBFS. -18 is the usual alignment level for spoken word.</param>
    public static float[] ReferenceTone(int sampleRate, double seconds = 2.0, double frequencyHz = 1000, double dbfs = -18)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleRate, 0);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);

        var count = (int)(sampleRate * seconds);
        var samples = new float[count];
        var peak = Math.Pow(10, dbfs / 20.0);
        var step = 2.0 * Math.PI * frequencyHz / sampleRate;

        // 10 ms raised-cosine edges, for the same reason clip export uses them: a tone that starts
        // and stops on a discontinuity clicks, and a click is exactly what a calibration tone must
        // not contain.
        var edge = Math.Min(count / 2, (int)(sampleRate * 0.01));

        for (var i = 0; i < count; i++)
        {
            var gain = 1.0;

            if (edge > 0 && i < edge)
            {
                gain = 0.5 * (1 - Math.Cos(Math.PI * i / edge));
            }
            else if (edge > 0 && i >= count - edge)
            {
                gain = 0.5 * (1 - Math.Cos(Math.PI * (count - 1 - i) / edge));
            }

            samples[i] = (float)(Math.Sin(step * i) * peak * gain);
        }

        return samples;
    }

    /// <summary>
    /// Sums one decaying sine per <paramref name="partials"/> entry, each starting at its own
    /// offset, and normalises so a two-note chime is no louder than a one-note tick.
    /// </summary>
    private static float[] Tone(
        int sampleRate, float amplitude, double seconds, params (double Hz, double StartSeconds)[] partials)
    {
        var count = Math.Max(1, (int)(sampleRate * seconds));
        var samples = new float[count];

        foreach (var (hz, startSeconds) in partials)
        {
            var start = (int)(startSeconds * sampleRate);
            var step = 2.0 * Math.PI * hz / sampleRate;

            // Decay chosen so the tail is inaudible by the end of the buffer; a cue that is still
            // ringing when the next one starts turns a count-in into a chord.
            var decay = 14.0 / Math.Max(0.001, seconds - startSeconds);

            for (var i = start; i < count; i++)
            {
                var t = (i - start) / (double)sampleRate;
                samples[i] += (float)(Math.Sin(step * (i - start)) * Math.Exp(-decay * t));
            }
        }

        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        if (peak > 0)
        {
            var scale = amplitude / peak;
            for (var i = 0; i < count; i++)
            {
                samples[i] *= scale;
            }
        }

        return samples;
    }
}
