using PoChopAudio.Shared;

namespace PoChopAudio.API.Features.Chop;

/// <summary>
/// Binds the export knobs off the download URL's query string. They live here rather than on
/// <see cref="ChopOptions"/> because they change nothing about detection — folding them in would
/// make every fade tweak re-run the whole analysis for an answer that cannot have changed.
///
/// Every property is nullable so an absent parameter means "leave the default alone", which is
/// what keeps a bare download URL a plain unprocessed slice.
/// </summary>
public sealed class ChopExportQuery
{
    public NormalizeMode? Normalize { get; set; }

    public double? TargetDb { get; set; }

    public double? CeilingDb { get; set; }

    public double? FadeInMs { get; set; }

    public double? FadeOutMs { get; set; }

    public ExportOptions ToOptions()
    {
        var mode = Normalize ?? NormalizeMode.None;

        return new ExportOptions
        {
            Normalize = mode,
            TargetDb = TargetDb ?? ExportLimits.DefaultTargetFor(mode),
            CeilingDb = CeilingDb ?? ExportLimits.DefaultCeilingDb,
            FadeInMs = FadeInMs ?? 0,
            FadeOutMs = FadeOutMs ?? 0,
        };
    }

    /// <summary>Field-keyed errors, empty when the query is usable.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();

        Check(
            nameof(TargetDb),
            TargetDb is null or (>= ExportLimits.MinTargetDb and <= ExportLimits.MaxTargetDb),
            $"Target must be {ExportLimits.MinTargetDb} to {ExportLimits.MaxTargetDb}.");

        Check(
            nameof(CeilingDb),
            CeilingDb is null or (>= ExportLimits.MinCeilingDb and <= ExportLimits.MaxCeilingDb),
            $"Ceiling must be {ExportLimits.MinCeilingDb} to {ExportLimits.MaxCeilingDb} dBFS.");

        Check(
            nameof(FadeInMs),
            FadeInMs is null or (>= 0 and <= ExportLimits.MaxFadeMs),
            $"Fade in must be 0-{ExportLimits.MaxFadeMs} ms.");

        Check(
            nameof(FadeOutMs),
            FadeOutMs is null or (>= 0 and <= ExportLimits.MaxFadeMs),
            $"Fade out must be 0-{ExportLimits.MaxFadeMs} ms.");

        return errors;

        void Check(string field, bool ok, string message)
        {
            if (!ok)
            {
                errors[field] = [message];
            }
        }
    }
}
