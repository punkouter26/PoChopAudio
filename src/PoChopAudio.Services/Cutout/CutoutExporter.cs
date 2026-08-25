using System.IO.Compression;
using PoChopAudio.Shared;

namespace PoChopAudio.Services.Cutout;

/// <summary>Writes the cutout PNG bytes for one job, either as a single image or a flat ZIP.</summary>
public static class CutoutExporter
{
    private const string PngContentType = "image/png";
    private const string ZipContentType = "application/zip";

    public static byte[] RenderPng(CutoutJob job, BackgroundColor? background)
    {
        var rgba = EdgeProcessor.Apply(job.Rgba, job.Width, job.Height, new CutoutOptions { Background = background });
        return ImageDecoder.EncodePng(rgba, job.Width, job.Height, background);
    }

    public static byte[] RenderPng(CutoutJob job, CutoutOptions options)
    {
        var rgba = EdgeProcessor.Apply(job.Rgba, job.Width, job.Height, options);
        return ImageDecoder.EncodePng(rgba, job.Width, job.Height, options.Background);
    }

    public static byte[] RenderZip(IEnumerable<CutoutJob> jobs, IReadOnlyDictionary<CutoutJobId, CutoutOptions> optionsById)
        => RenderZip(jobs, optionsById, template: null);

    public static byte[] RenderZip(
        IEnumerable<CutoutJob> jobs,
        IReadOnlyDictionary<CutoutJobId, CutoutOptions> optionsById,
        string? template)
    {
        var jobList = jobs.ToList();
        var stems = UniqueStems(jobList.Select(j => j.OriginalFileName).ToArray());
        var pattern = string.IsNullOrWhiteSpace(template) ? null : template;

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < jobList.Count; i++)
            {
                var job = jobList[i];
                var opts = optionsById.TryGetValue(job.Id, out var o) ? o : new CutoutOptions();
                var bytes = RenderPng(job, opts);
                var name = pattern is null
                    ? ClipFileName(stems[i])
                    : ApplyTemplate(pattern, stems[i], i + 1, jobList.Count);
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }

        return zipStream.ToArray();
    }

    public static string ClipFileName(string stem) => $"{stem}_cutout.png";

    /// <summary>
    /// Applies a user-supplied naming template. Recognised tokens: {stem}, {index}, {index:00},
    /// {index:000}, {total}, {date}. The result is always forced to end in .png.
    /// </summary>
    public static string ApplyTemplate(string template, string stem, int index, int total)
    {
        var result = template
            .Replace("{stem}", stem)
            .Replace("{index}", index.ToString())
            .Replace("{index:00}", index.ToString("D2"))
            .Replace("{index:000}", index.ToString("D3"))
            .Replace("{total}", total.ToString())
            .Replace("{date}", DateTime.UtcNow.ToString("yyyy-MM-dd"));

        if (!result.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            result += ".png";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return result;
    }

    public static string Stem(string originalFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        // Pixel Motion Photo files end with .MP — strip the .MP too.
        if (stem.EndsWith(".MP", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^3];
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(invalid, '_');
        }

        stem = stem.Trim();
        return stem.Length == 0 ? "cutout" : stem;
    }

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
}
