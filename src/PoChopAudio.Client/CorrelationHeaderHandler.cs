namespace PoChopAudio.Client;

public sealed class CorrelationHeaderHandler : DelegatingHandler
{
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains("X-Session-ID"))
        {
            request.Headers.Add("X-Session-ID", SessionId);
        }

        if (!request.Headers.Contains("X-Correlation-ID"))
        {
            request.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString("N"));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
