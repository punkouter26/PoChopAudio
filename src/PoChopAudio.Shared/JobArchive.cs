namespace PoChopAudio.Shared;

/// <summary>
/// Lightweight per-batch metadata persisted to Azurite. The original uploads and processed
/// outputs live as opaque bytes; this type holds the *index* needed to surface a batch the
/// client is currently holding, and to "re-open" a session after the server restarts.
/// </summary>
public sealed record BatchEntry(
    string BatchId,
    string Feature,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<JobRef> Jobs);

public sealed record JobRef(
    string JobId,
    string OriginalFileName,
    string ContentType,
    long Bytes,
    int Width,
    int Height,
    double DurationSeconds,
    int SampleRate,
    int Channels,
    IReadOnlyList<ClipRef> Clips);

public sealed record ClipRef(
    int Index,
    double StartSeconds,
    double EndSeconds);
