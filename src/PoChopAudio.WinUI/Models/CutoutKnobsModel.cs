using CommunityToolkit.Mvvm.ComponentModel;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.Models;

public partial class CutoutKnobsModel : ObservableObject
{
    [ObservableProperty]
    private CutoutEngine? _engine = CutoutEngine.OnnxU2Net;

    [ObservableProperty]
    // Tuned for head shots. u2netp returns a soft mask, so at threshold 0 every faint pixel
    // survives as a halo; 160 cuts the haze, the 1 px erode pulls the edge inside the fringe,
    // the 1 px feather keeps that edge from looking jagged, and the 1.6x multiplier saturates
    // what is left so the head is solid rather than translucent.
    private byte _alphaThreshold = 160;

    [ObservableProperty]
    private int _featherRadius = 1;

    [ObservableProperty]
    private int _morphology = -1;

    [ObservableProperty]
    private double _alphaMultiplier = 1.6;

    [ObservableProperty]
    private string? _backgroundColorHex;

    [ObservableProperty]
    private bool _trimTransparentEdges;

    [ObservableProperty]
    private bool _headOnly = true;

    [ObservableProperty]
    private int _trimPaddingPx = 24;

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
            HeadOnly = HeadOnly,
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
            HeadOnly = HeadOnly,
            TrimPaddingPx = TrimPaddingPx,
        };
    }
}

