using System.Globalization;
using PoChopAudio.Shared;

namespace PoChopAudio.Client.Models;

/// <summary>
/// The export knobs as the UI holds them, and the query string they turn into.
///
/// Export settings are batch-wide on purpose. Unlike the detection knobs, they say nothing about
/// a particular recording — a fade is a fade — so per-file overrides would add a second pinning
/// rule to explain for no real gain. They also never trigger a re-split: they only change the
/// download URLs, which is why touching them is instant.
/// </summary>
public sealed class ExportSettings
{
    public NormalizeMode Normalize { get; set; } = NormalizeMode.None;

    public double TargetDb { get; set; } = ExportLimits.DefaultPeakTargetDb;

    public double CeilingDb { get; set; } = ExportLimits.DefaultCeilingDb;

    public double FadeInMs { get; set; }

    public double FadeOutMs { get; set; }

    public bool IsDefault =>
        Normalize is NormalizeMode.None && FadeInMs <= 0 && FadeOutMs <= 0;

    /// <summary>Units for the target, which is dBFS for the sample-based modes and LUFS for loudness.</summary>
    public string TargetUnit => Normalize is NormalizeMode.Lufs ? "LUFS" : "dBFS";

    /// <summary>Moves the target to the sensible default for a newly picked mode.</summary>
    public void AdoptDefaultTargetFor(NormalizeMode mode)
    {
        Normalize = mode;
        TargetDb = ExportLimits.DefaultTargetFor(mode);
    }

    public ExportSettings Clone() => (ExportSettings)MemberwiseClone();

    /// <summary>
    /// Query fragment for a download URL, without a leading separator. Empty when nothing would
    /// change, so an untouched batch keeps the short URLs it always had.
    /// </summary>
    public string ToQuery()
    {
        var parts = new List<string>(5);

        if (Normalize is not NormalizeMode.None)
        {
            parts.Add($"normalize={Normalize}");
            parts.Add($"targetDb={Format(TargetDb)}");
            parts.Add($"ceilingDb={Format(CeilingDb)}");
        }

        if (FadeInMs > 0)
        {
            parts.Add($"fadeInMs={Format(FadeInMs)}");
        }

        if (FadeOutMs > 0)
        {
            parts.Add($"fadeOutMs={Format(FadeOutMs)}");
        }

        return string.Join('&', parts);
    }

    // InvariantGlobalization is on, but be explicit: a comma decimal separator would silently
    // become a second query value and the server would read a different number than the slider shows.
    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
