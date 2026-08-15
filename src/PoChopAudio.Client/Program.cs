using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoChopAudio.Client;
using PoChopAudio.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddTransient<CorrelationHeaderHandler>();
builder.Services.AddScoped(sp =>
{
    var handler = sp.GetRequiredService<CorrelationHeaderHandler>();
    handler.InnerHandler = new HttpClientHandler();
    return new HttpClient(handler) { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
});
builder.Services.AddScoped<CutoutClient>();
builder.Services.AddScoped<ProgressStream>();
builder.Services.AddSingleton<BrowserOnnxRuntime>();
builder.Services.AddScoped<PreviewService>();
builder.Services.AddScoped<DropZoneService>();

await builder.Build().RunAsync();
