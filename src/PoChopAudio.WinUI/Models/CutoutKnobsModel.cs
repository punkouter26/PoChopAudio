using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Models;

public partial class CutoutKnobsModel : ObservableObject
{
    [ObservableProperty]
    private CutoutEngine? _engine = CutoutEngine.OnnxU2Net;

    [ObservableProperty]
    private byte _alphaThreshold = CutoutLimits.DefaultAlphaThreshold;

    [ObservableProperty]
    private int _featherRadius = CutoutLimits.DefaultFeatherRadius;

    [ObservableProperty]
    private int _morphology = CutoutLimits.DefaultMorphology;

    [ObservableProperty]
    private double _alphaMultiplier = CutoutLimits.DefaultAlphaMultiplier;

    [ObservableProperty]
    private string? _backgroundColorHex;

    [ObservableProperty]
    private bool _trimTransparentEdges;

    [ObservableProperty]
    private int _trimPaddingPx = 16;

    public CutoutOptions ToOptions()
    {
        BackgroundColor? bg = null;
        if (!string.IsNullOrWhiteSpace(BackgroundColorHex) && BackgroundColorHex.StartsWith('#') && BackgroundColorHex.Length == 7)
        {
            if (byte.TryParse(BackgroundColorHex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(BackgroundColorHex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(BackgroundColorHex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                bg = new BackgroundColor(r, g, b);
            }
        }

        return new CutoutOptions
        {
            Engine = Engine,
            AlphaThreshold = AlphaThreshold,
            FeatherRadius = FeatherRadius,
            Morphology = Morphology,
            AlphaMultiplier = AlphaMultiplier,
            Background = bg,
            TrimTransparentEdges = TrimTransparentEdges,
            TrimPaddingPx = TrimPaddingPx,
        };
    }

    public CutoutKnobsModel Clone()
    {
        return new CutoutKnobsModel
        {
            Engine = Engine,
            AlphaThreshold = AlphaThreshold,
            FeatherRadius = FeatherRadius,
            Morphology = Morphology,
            AlphaMultiplier = AlphaMultiplier,
            BackgroundColorHex = BackgroundColorHex,
            TrimTransparentEdges = TrimTransparentEdges,
            TrimPaddingPx = TrimPaddingPx,
        };
    }
}

