namespace PoChopAudio.Client.Models;

/// <summary>
/// A finished recording on its way into the batch. It carries a name because, unlike an upload,
/// there is no filename to take the clip stem from — <c>Nick_Happy</c> here is what makes the
/// clips come out as Nick_Happy_1.wav … Nick_Happy_5.wav.
/// </summary>
/// <param name="Name">Stem for the clips, without an extension.</param>
/// <param name="Wav">16-bit PCM WAV bytes as encoded in the browser.</param>
public sealed record RecordedTake(string Name, byte[] Wav, double DurationSeconds);
