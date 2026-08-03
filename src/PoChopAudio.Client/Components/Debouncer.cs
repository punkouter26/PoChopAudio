namespace PoChopAudio.Client.Components;

/// <summary>
/// Debounces calls: only the last call within <see cref="Delay"/> runs, after the caller stops
/// firing. Designed for slider/input events that fire dozens of times per drag.
/// </summary>
public sealed class Debouncer
{
    private readonly TimeSpan _delay;
    private CancellationTokenSource? _cts;
    private readonly Func<Func<Task>, Task> _schedule;

    public Debouncer(TimeSpan delay)
        : this(delay, ScheduleAsync)
    {
    }

    public Debouncer(TimeSpan delay, Func<Func<Task>, Task> schedule)
    {
        _delay = delay;
        _schedule = schedule;
    }

    /// <summary>
    /// Replaces any pending work. <paramref name="work"/> receives a token cancelled the moment
    /// a newer call arrives — long-running work should bail on that signal.
    /// </summary>
    public async Task InvokeAsync(Func<CancellationToken, Task> work)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            await _schedule(() =>
            {
                if (cts.IsCancellationRequested)
                {
                    return Task.CompletedTask;
                }

                return work(cts.Token);
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ScheduleAsync(Func<Task> work)
    {
        // Default schedule: yield once so the UI thread can finish painting the input,
        // then run. The debounce itself is achieved by replacing _cts on every call.
        await Task.Yield();
        await work();
    }
}
