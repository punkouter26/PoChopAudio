using Microsoft.AspNetCore.Http.Features;
using PoChopAudio.API.Features.Archive;
using PoChopAudio.API.Features.Chop;
using PoChopAudio.API.Features.Cutout;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Services.Cutout.Engines;
using PoChopAudio.API.Features.Diagnostics;
using PoChopAudio.API.Storage;
using PoChopAudio.Shared;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<ChopJobStore>();
builder.Services.AddHostedService<ChopJobCleanup>();

builder.Services.AddSingleton<CutoutJobStore>();
builder.Services.AddHostedService<CutoutJobCleanup>();
builder.Services.AddSingleton(new CutoutModelOptions(
    Path.Combine(builder.Environment.ContentRootPath, "Content", "Models", "u2netp.onnx")));
builder.Services.AddSingleton<IBackgroundRemover, OnnxU2NetRemover>();
builder.Services.AddSingleton<EnginePicker>();

builder.Services.AddSingleton<AzuriteBlobStore>();
builder.Services.AddSingleton<JobArchive>();
builder.Services.AddSingleton<ProgressChannel>();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = Math.Max(ChopLimits.MaxUploadBytes, CutoutLimits.MaxUploadBytes);
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = Math.Max(ChopLimits.MaxUploadBytes, CutoutLimits.MaxUploadBytes);
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddAuthentication("FakeAuth")
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, PoChopAudio.API.Features.Auth.FakeAuthHandler>("FakeAuth", null);
    builder.Services.AddAuthorization();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapDiagnosticsEndpoints();
app.MapChopEndpoints();
app.MapCutoutEndpoints();
app.MapArchiveEndpoints();
app.MapFallbackToFile("index.html");

app.Run();
