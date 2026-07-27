using System.IO.Compression;
using NAudio.Wave;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Chop;

/// <summary>Cuts clips out of a job's canonical WAV and writes them as 16-bit PCM WAV.</summary>
public static class ClipExporter
{
    private const int BitsPerSample = 16;
    private const string FallbackStem = "clip";

    public static byte[] RenderClip(string canonicalPath, ChopSegment segment)
    {
        using var reader = new WaveFileReader(canonicalPath);
        return RenderClip(reader, segment);
    }

    public static byte[] RenderZip(ChopJob job)
    {
        using var reader = new WaveFileReader(job.CanonicalPath);
        var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntries(archive, reader, Stem(job.OriginalFileName), job.Segments);
        }

        return zipStream.ToArray();
    }

    /// <summary>
    /// Packs the clips of several jobs into one flat ZIP. Every clip keeps its source name as the
    /// prefix, so the archive reads like the output folder: Nick_Happy_1.wav … Nick_Sad_5.wav.
    /// </summary>
    public static byte[] RenderZip(IReadOnlyList<ChopJob> jobs)
    {
        var stems = UniqueStems([.. jobs.Select(j => j.OriginalFileName)]);
        var zipStream = new MemoryStream();

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < jobs.Count; i++)
            {
                using var reader = new WaveFileReader(jobs[i].CanonicalPath);
                WriteEntries(archive, reader, stems[i], jobs[i].Segments);
            }
        }

        return zipStream.ToArray();
    }

    public static string ClipFileName(ChopJob job, ChopSegment segment) =>
        ClipFileName(Stem(job.OriginalFileName), segment);

    // Source name as the prefix, take number as the suffix: Matt_Happy_1.wav … Matt_Happy_5.wav.
    public static string ClipFileName(string stem, ChopSegment segment) => $"{stem}_{segment.Index}.wav";

    /// <summary>Strips the extension and anything a file system would reject.</summary>
    public static string Stem(string originalFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalid, '_');
        }

        stem = stem.Trim();
        return stem.Length == 0 ? FallbackStem : stem;
    }

    /// <summary>
    /// Gives every source a distinct stem so a flat ZIP cannot drop a clip to a name collision —
    /// two uploads called Nick_Happy.m4a become Nick_Happy and Nick_Happy(2).
    /// </summary>
    public static IReadOnlyList<string> UniqueStems(IReadOnlyList<string> originalFileNames)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stems = new string[originalFileNames.Count];

        for (var i = 0; i < originalFileNames.Count; i++)
        {
            var stem = Stem(originalFileNames[i]);
            var candidate = stem;

            for (var suffix = 2; !taken.Add(candidate); suffix++)
            {
                candidate = $"{stem}({suffix})";
            }

            stems[i] = candidate;
        }

        return stems;
    }

    private static void WriteEntries(
        ZipArchive archive,
        WaveFileReader reader,
        string stem,
        IReadOnlyList<ChopSegment> segments)
    {
        foreach (var segment in segments)
        {
            var entry = archive.CreateEntry(ClipFileName(stem, segment), CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            entryStream.Write(RenderClip(reader, segment));
        }
    }

    private static byte[] RenderClip(WaveFileReader reader, ChopSegment segment)
    {
        var format = reader.WaveFormat;
        var totalFrames = reader.SampleCount;

        var startFrame = Math.Clamp((long)(segment.StartSeconds * format.SampleRate), 0, totalFrames);
        var endFrame = Math.Clamp((long)Math.Ceiling(segment.EndSeconds * format.SampleRate), startFrame, totalFrames);

        reader.Position = startFrame * format.BlockAlign;

        var samples = reader.ToSampleProvider();
        var remaining = (endFrame - startFrame) * format.Channels;
        var buffer = new float[format.SampleRate * format.Channels];

        var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, new WaveFormat(format.SampleRate, BitsPerSample, format.Channels)))
        {
            while (remaining > 0)
            {
                var want = (int)Math.Min(remaining, buffer.Length);
                var read = samples.Read(buffer, 0, want);
                if (read == 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    writer.WriteSample(Math.Clamp(buffer[i], -1f, 1f));
                }

                remaining -= read;
            }
        }

        return output.ToArray();
    }
}
