using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Models;

public partial class ExportKnobsModel : ObservableObject
{
    [ObservableProperty]
    private NormalizeMode _normalize = NormalizeMode.None;

    [ObservableProperty]
    private double _targetDb = ExportLimits.DefaultPeakTargetDb;

    [ObservableProperty]
    private double _ceilingDb = ExportLimits.DefaultCeilingDb;

    [ObservableProperty]
    private double _fadeInMs = 0;

    [ObservableProperty]
    private double _fadeOutMs = 0;

    partial void OnNormalizeChanged(NormalizeMode value)
    {
        TargetDb = ExportLimits.DefaultTargetFor(value);
    }

    public ExportOptions ToOptions() => new()
    {
        Normalize = Normalize,
        TargetDb = TargetDb,
        CeilingDb = CeilingDb,
        FadeInMs = FadeInMs,
        FadeOutMs = FadeOutMs,
    };

    public string ToQueryString()
    {
        if (Normalize == NormalizeMode.None && FadeInMs <= 0 && FadeOutMs <= 0)
        {
            return string.Empty;
        }

        var parts = new List<string> { $"normalize={Normalize}" };
        if (Normalize != NormalizeMode.None)
        {
            parts.Add($"targetDb={TargetDb:F1}");
            parts.Add($"ceilingDb={CeilingDb:F1}");
        }
        if (FadeInMs > 0) parts.Add($"fadeInMs={FadeInMs:F0}");
        if (FadeOutMs > 0) parts.Add($"fadeOutMs={FadeOutMs:F0}");

        return string.Join('&', parts);
    }
}

