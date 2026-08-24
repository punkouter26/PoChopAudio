using System.Net.Http.Headers;

namespace PoChopAudio.WinUI.Services;

public interface IApiConfiguration
{
    string BaseUrl { get; set; }
    string SessionId { get; }
    HttpClient CreateClient();
}

public sealed class ApiConfiguration : IApiConfiguration
{
    public string BaseUrl { get; set; } = "http://localhost:5177";
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/")
        };
        client.DefaultRequestHeaders.Add("X-Session-ID", SessionId);
        client.DefaultRequestHeaders.Add("X-Correlation-ID", Guid.NewGuid().ToString("N"));
        return client;
    }
}

