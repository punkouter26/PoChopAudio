using System.Net.Http.Json;

namespace PoChopAudio.WinUI.Services;

public sealed record DiagnosticInfo(
    string Host,
    string Environment,
    string OsDescription,
    string FrameworkDescription,
    long WorkingSetBytes,
    IReadOnlyList<string> AudioCodecs,
    bool AzuriteReachable,
    bool OnnxModelPresent);

public sealed class DiagnosticsApiClient(IApiConfiguration config)
{
    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            var response = await client.GetAsync("health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetDiagJsonAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = config.CreateClient();
            var response = await client.GetAsync("diag", cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }
}

