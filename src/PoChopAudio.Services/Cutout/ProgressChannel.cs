using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PoChopAudio.Services.Cutout;

/// <summary>
/// A tiny per-job progress broadcaster. The analyze endpoint writes phase updates ("decoding",
/// "inferring", "trimming", "encoding") and the SSE endpoint reads them. One channel per job
/// is created on demand and pruned when the job is removed or the stream is closed.
/// </summary>
public sealed class ProgressChannel
{
    private readonly ConcurrentDictionary<Guid, Channel<ProgressUpdate>> _channels = new();

    public ChannelReader<ProgressUpdate> Subscribe(Guid jobId)
    {
        var channel = _channels.GetOrAdd(jobId, _ => Channel.CreateUnbounded<ProgressUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        }));
        return channel.Reader;
    }

    public void Publish(Guid jobId, string phase, double percent)
    {
        if (_channels.TryGetValue(jobId, out var channel))
        {
            channel.Writer.TryWrite(new ProgressUpdate(DateTimeOffset.UtcNow, phase, percent));
        }
    }

    public void Complete(Guid jobId)
    {
        if (_channels.TryRemove(jobId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Abandon(Guid jobId)
    {
        if (_channels.TryRemove(jobId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }
}

public sealed record ProgressUpdate(DateTimeOffset At, string Phase, double Percent);
