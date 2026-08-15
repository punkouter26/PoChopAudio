using System.Collections.Concurrent;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Chop;

/// <summary>One uploaded recording, its decoded audio on disk, and the latest split of it.</summary>
public sealed class ChopJob
{
    public required JobId Id { get; init; }
    public required string OriginalFileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    public string CanonicalPath => Path.Combine(WorkingDirectory, "canonical.wav");

    public AudioEnvelope? Envelope { get; set; }

    public IReadOnlyList<ChopSegment> Segments { get; set; } = [];

    /// <summary>Offset in seconds from the start of the original recording to the trimmed start.</summary>
    public double TrimStart { get; set; }

    /// <summary>Offset in seconds from the trimmed end to the end of the original recording.</summary>
    public double TrimEnd { get; set; }
}

/// <summary>
/// Keeps decoded uploads on local disk for the length of a working session. Nothing here survives a
/// restart by design — a job is scratch space between "upload" and "download the clips".
/// </summary>
public sealed class ChopJobStore : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<JobId, ChopJob> _jobs = new();

    public ChopJobStore()
    {
        Root = Path.Combine(Path.GetTempPath(), "PoChopAudio");
        TryDeleteDirectory(Root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public int Count => _jobs.Count;

    public ChopJob Create(string originalFileName)
    {
        var id = JobId.New();
        var directory = Path.Combine(Root, id.ToString());
        Directory.CreateDirectory(directory);

        var job = new ChopJob
        {
            Id = id,
            OriginalFileName = originalFileName,
            WorkingDirectory = directory,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        _jobs[id] = job;
        return job;
    }

    public ChopJob? Find(string? rawJobId) =>
        JobId.TryParse(rawJobId, out var id) && _jobs.TryGetValue(id, out var job) ? job : null;

    public void Remove(JobId id)
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

/// <summary>Sweeps abandoned jobs so a long-running instance does not fill the temp drive.</summary>
public sealed class ChopJobCleanup(ChopJobStore store, ILogger<ChopJobCleanup> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var removed = store.RemoveExpired(DateTimeOffset.UtcNow);
            if (removed > 0)
            {
                ChopLog.JobsExpired(logger, removed);
            }
        }
    }
}
