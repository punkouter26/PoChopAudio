using Microsoft.AspNetCore.Http.Features;
using PoChopAudio.API.Features.Chop;
using PoChopAudio.API.Features.Diagnostics;
using PoChopAudio.Shared;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<ChopJobStore>();
builder.Services.AddHostedService<ChopJobCleanup>();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = ChopLimits.MaxUploadBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = ChopLimits.MaxUploadBytes;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapDiagnosticsEndpoints();
app.MapChopEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
