using PoChopAudio.Services.Cutout;

namespace PoChopAudio.API.Features.Cutout;

/// <summary>Sweeps abandoned cutout jobs so a long-running instance does not fill the temp drive.</summary>
public sealed class CutoutJobCleanup(CutoutJobStore store, ILogger<CutoutJobCleanup> logger) : BackgroundService
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
                CutoutLog.JobsExpired(logger, removed);
            }
        }
    }
}
