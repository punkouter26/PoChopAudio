using PoChopAudio.Shared;
using PoChopAudio.Services.Chop;

namespace PoChopAudio.API.Features.Chop;

/// <summary>
/// Binds the export knobs off the download URL's query string. They live here rather than on
/// <see cref="ChopOptions"/> because they change nothing about detection — folding them in would
/// make every fade tweak re-run the whole analysis for an answer that cannot have changed.
///
/// Every property is nullable so an absent parameter means "leave the default alone", which is
/// what keeps a bare download URL a plain unprocessed slice. Range checking happens once the
/// nulls are resolved, in <c>ChopService.ValidateExport</c>, so the desktop client gets it too.
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
}
