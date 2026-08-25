using System.Collections.Concurrent;
using PoChopAudio.Shared;

namespace PoChopAudio.Services.Cutout;

/// <summary>One uploaded image and the RGBA pixels decoded from it. Output is rendered on demand.</summary>
public sealed class CutoutJob
{
    public required CutoutJobId Id { get; init; }
    public required string OriginalFileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    public string SourcePath => Path.Combine(WorkingDirectory, "source");

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; set; }
    public required string ContentType { get; init; }

    public CutoutResult? LastResult { get; set; }
}

/// <summary>
/// Keeps decoded images on local disk for the length of a working session. Nothing here survives
/// a restart by design — a job is scratch space between upload and download.
/// </summary>
public sealed class CutoutJobStore : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<CutoutJobId, CutoutJob> _jobs = new();

    public CutoutJobStore()
    {
        // Private subdirectory per instance — see the note in ChopJobStore. Wiping the shared
        // parent meant a second instance deleted the first one's images while it was serving them.
        var parent = Path.Combine(Path.GetTempPath(), "PoChopAudioCutout");
        Directory.CreateDirectory(parent);
        SweepStaleSiblings(parent, Lifetime);

        Root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public int Count => _jobs.Count;

    public CutoutJob Create(
        string originalFileName,
        int width,
        int height,
        byte[] rgba,
        string contentType)
    {
        var id = CutoutJobId.New();
        var directory = Path.Combine(Root, id.ToString());
        Directory.CreateDirectory(directory);

        var job = new CutoutJob
        {
            Id = id,
            OriginalFileName = originalFileName,
            WorkingDirectory = directory,
            CreatedUtc = DateTimeOffset.UtcNow,
            Width = width,
            Height = height,
            Rgba = rgba,
            ContentType = contentType,
        };

        _jobs[id] = job;
        return job;
    }

    public CutoutJob? Find(string? rawJobId) =>
        CutoutJobId.TryParse(rawJobId, out var id) && _jobs.TryGetValue(id, out var job) ? job : null;

    public void Remove(CutoutJobId id)
    {
        if (_jobs.TryRemove(id, out var job))
        {
            TryDeleteDirectory(job.WorkingDirectory);
        }
    }

    public int RemoveExpired(DateTimeOffset now)
    {
        var removed = 0;
        foreach (var job in _jobs.Values.Where(j => now - j.CreatedUtc > Lifetime).ToArray())
        {
            Remove(job.Id);
            removed++;
        }

        return removed;
    }

    public void Dispose() => TryDeleteDirectory(Root);

    /// <summary>
    /// Clears leftovers from instances that died without disposing. Only directories older than the
    /// job lifetime go, since those would have expired anyway — a live peer is never touched.
    /// </summary>
    private static void SweepStaleSiblings(string parent, TimeSpan lifetime)
    {
        try
        {
            var cutoff = DateTime.UtcNow - lifetime;
            foreach (var directory in Directory.EnumerateDirectories(parent))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file is still open somewhere; the next startup sweeps it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
