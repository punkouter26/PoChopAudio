using System.Reflection;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Diagnostics;

public static class DiagnosticsEndpoints
{
    private static readonly string[] SecretMarkers =
        ["secret", "password", "pwd", "key", "token", "connectionstring", "credential", "sas", "signature"];

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => TypedResults.Ok(new { status = "healthy" }))
            .WithTags("Diagnostics")
            .WithSummary("Liveness probe");

        // /diag is gated to Development on purpose — it leaks the temp-root path, format
        // support and runtime fingerprint. /health is enough for a liveness probe in any env.
        var diag = app.MapGet("/diag", (IHostEnvironment environment, IConfiguration configuration, ChopJobStore store, EnginePicker engines) =>
            TypedResults.Ok(new
            {
                application = environment.ApplicationName,
                environment = environment.EnvironmentName,
                version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                runtime = Environment.Version.ToString(),
                os = Environment.OSVersion.ToString(),
                utcNow = DateTimeOffset.UtcNow,
                audio = new
                {
                    supportedFormats = AudioDecoder.SupportedExtensionsDescription,
                    supportedExtensions = AudioDecoder.SupportedExtensions,
                    maxUploadMb = ChopLimits.MaxUploadBytes / (1024 * 1024),
                    maxBatchFiles = ChopLimits.MaxBatchFiles,
                    frameMs = SegmentDetector.FrameMs,
                },
                image = new
                {
                    supportedFormats = string.Join(", ", CutoutLimits.AcceptedExtensions),
                    supportedExtensions = CutoutLimits.AcceptedExtensions,
                    maxUploadMb = CutoutLimits.MaxUploadBytes / (1024 * 1024),
                    maxBatchFiles = CutoutLimits.MaxBatchFiles,
                    maxDimension = CutoutLimits.MaxDimension,
                    availableEngines = engines.Snapshot(),
                },
                jobs = new
                {
                    active = store.Count,
                    lifetimeHours = ChopJobStore.Lifetime.TotalHours,
                    storageRoot = store.Root,
                },
                configuration = MaskedConfiguration(configuration),
            }))
            .WithTags("Diagnostics")
            .WithSummary("Masked configuration and runtime state");

        if (!app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            diag.RequireHost("localhost");
        }

        return app;
    }

    private static SortedDictionary<string, string?> MaskedConfiguration(IConfiguration configuration)
    {
        var masked = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configuration.AsEnumerable().Where(e => e.Value is not null))
        {
            masked[entry.Key] = IsSecret(entry.Key) ? Mask(entry.Value) : entry.Value;
        }

        return masked;
    }

    private static bool IsSecret(string key) =>
        SecretMarkers.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "***" : $"***({value.Length} chars)";
}
