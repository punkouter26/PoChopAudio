using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using PoChopAudio.Services.Dsp;
using PoChopAudio.Services.Chop;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Models;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace PoChopAudio.WinUI.Controls;

/// <summary>
/// The recording, drawn on the GPU: segment bands, an amplitude trace or a spectrogram, and a
/// playhead that lives on the compositor thread.
///
/// <para>
/// Everything here is one Win2D <see cref="CanvasControl"/> and one Composition visual. The
/// previous implementation built a XAML <c>Line</c> per waveform bar and a <c>Border</c> plus
/// <c>TextBlock</c> per segment, cleared and rebuilt on every redraw; a batch of ten files put
/// several thousand elements into the visual tree to show a picture that is not interactive at the
/// element level and never needed to be.
/// </para>
/// </summary>
public sealed partial class WaveformView : UserControl
{
    private const float FooterHeight = 18f;

    // Colours are held as Win2D colours rather than brushes because a drawing session takes colours
    // directly; a ThemeResource lookup per bar was never going to be the right shape here.
    private static readonly Color SegmentEvenFill = Color.FromArgb(60, 59, 130, 246);
    private static readonly Color SegmentOddFill = Color.FromArgb(60, 16, 185, 129);
    private static readonly Color SegmentEvenEdge = Color.FromArgb(190, 59, 130, 246);
    private static readonly Color SegmentOddEdge = Color.FromArgb(190, 16, 185, 129);

    private ChopFileItem? _item;
    private CanvasControl? _surface;
    private CanvasTextFormat? _badgeFormat;
    private SpriteVisual? _playhead;
    private bool _isScrubbing;

    /// <summary>Raised on click or on the end of a drag, with the segment under the pointer if any.</summary>
    public event Action<ChopFileItem, ChopSegment?>? SegmentClicked;

    /// <summary>Raised while dragging, with a position in seconds. Lets the page seek playback.</summary>
    public event Action<ChopFileItem, double>? Scrubbed;

    public WaveformView()
    {
        InitializeComponent();

        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerReleased += OnPointerReleased;
        RootGrid.PointerExited += OnPointerExited;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(ChopFileItem), typeof(WaveformView),
            new PropertyMetadata(null, (d, e) => ((WaveformView)d).OnItemChanged(e.NewValue as ChopFileItem)));

    public ChopFileItem? Item
    {
        get => (ChopFileItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureSurface();
        EnsurePlayhead();
        Redraw();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        ReleaseSurface();
    }

    /// <summary>
    /// Creates the drawing surface if this control does not currently have one.
    /// <para>
    /// Called from <c>Loaded</c> rather than the constructor because the card lists virtualize: an
    /// element scrolled out of view is unloaded, has its surface released, and is later reloaded
    /// against a different item. A <see cref="CanvasControl"/> cannot be revived after
    /// <c>RemoveFromVisualTree</c>, so re-entering the tree means building a new one.
    /// </para>
    /// </summary>
    private void EnsureSurface()
    {
        if (_surface is not null)
        {
            return;
        }

        _surface = new CanvasControl { ClearColor = Colors.Transparent };
        _surface.Draw += OnDraw;
        _surface.CreateResources += OnCreateResources;

        // Index 0: everything else in the grid - playhead host, time markers, hover tip, busy
        // badge - is an overlay and has to stay above the drawing.
        RootGrid.Children.Insert(0, _surface);
    }

    private void ReleaseSurface()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.Draw -= OnDraw;
        _surface.CreateResources -= OnCreateResources;

        // Win2D holds a device per control; without this they are released only when the finalizer
        // eventually runs, and a scrolling batch creates and drops these often.
        _surface.RemoveFromVisualTree();
        _surface = null;
    }

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _badgeFormat = new CanvasTextFormat
        {
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
    }

    private void OnItemChanged(ChopFileItem? newItem)
    {
        if (_item is not null)
        {
            _item.PropertyChanged -= OnItemPropertyChanged;
        }

        _item = newItem;

        if (_item is not null)
        {
            _item.PropertyChanged += OnItemPropertyChanged;
        }

        Redraw();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ChopFileItem.Waveform):
            case nameof(ChopFileItem.Segments):
            case nameof(ChopFileItem.Spectrogram):
            case nameof(ChopFileItem.ShowSpectrogram):
                DispatcherQueue.TryEnqueue(Redraw);
                break;

            case nameof(ChopFileItem.IsBuildingSpectrogram):
                DispatcherQueue.TryEnqueue(() => BusyBadge.Visibility =
                    _item?.IsBuildingSpectrogram == true ? Visibility.Visible : Visibility.Collapsed);
                break;

            case nameof(ChopFileItem.PlayheadRatio):
            case nameof(ChopFileItem.IsPlaying):
                DispatcherQueue.TryEnqueue(UpdatePlayhead);
                break;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Redraw();
        UpdatePlayhead();
    }

    /// <summary>Marks the surface dirty and refreshes everything that is not drawn by Win2D.</summary>
    private void Redraw()
    {
        _surface?.Invalidate();

        if (_item is null)
        {
            StartTimeText.Text = "0:00.0";
            EndTimeText.Text = "0:00.0";
            AutomationProperties.SetName(RootBorder, "Waveform, empty");
            return;
        }

        StartTimeText.Text = "0:00.0";
        EndTimeText.Text = FormatTime(_item.DurationSeconds);

        var count = _item.Segments.Count;
        var mode = _item.ShowSpectrogram ? "Frequency view" : "Waveform";

        AutomationProperties.SetName(
            RootBorder,
            $"{mode} of {_item.FileName}, {_item.DurationSeconds:F1} seconds, {count} sound{(count == 1 ? string.Empty : "s")} found");
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_item is null)
        {
            return;
        }

        var session = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var plotHeight = Math.Max(1f, height - FooterHeight);

        if (_item.ShowSpectrogram && _item.Spectrogram is { } spectrogram)
        {
            DrawSpectrogram(session, sender, spectrogram, width, plotHeight);
        }

        DrawSegments(session, width, plotHeight);

        if (!_item.ShowSpectrogram)
        {
            DrawWaveform(session, width, plotHeight);
        }
    }

    private void DrawSegments(CanvasDrawingSession session, float width, float height)
    {
        var duration = _item!.DurationSeconds;

        if (duration <= 0 || _item.Segments.Count == 0)
        {
            return;
        }

        for (var i = 0; i < _item.Segments.Count; i++)
        {
            var segment = _item.Segments[i];
            var isEven = i % 2 == 0;

            var startX = (float)(segment.StartSeconds / duration * width);
            var endX = (float)(segment.EndSeconds / duration * width);
            var bandWidth = Math.Max(2f, endX - startX);

            var rectangle = new Rect(startX, 0, bandWidth, height);

            // Over a spectrogram the fill would wash out the picture underneath, so only the edges
            // are drawn there; over a waveform the tint is what makes the bands readable at all.
            if (!_item.ShowSpectrogram)
            {
                session.FillRoundedRectangle(rectangle, 3, 3, isEven ? SegmentEvenFill : SegmentOddFill);
            }

            var edge = isEven ? SegmentEvenEdge : SegmentOddEdge;
            session.DrawLine(startX, 0, startX, height, edge, 1.5f);
            session.DrawLine(startX + bandWidth, 0, startX + bandWidth, height, edge, 1.5f);

            if (_badgeFormat is not null && bandWidth > 46)
            {
                var label = $"Take {segment.Index} ({segment.DurationSeconds:F1}s)";
                var badge = new Rect(startX + 3, 3, Math.Min(bandWidth - 6, 96), 15);
                session.FillRoundedRectangle(badge, 3, 3, edge);
                session.DrawText(label, new Vector2((float)badge.X + 4, (float)badge.Y + 1), Colors.White, _badgeFormat);
            }
        }
    }

    private void DrawWaveform(CanvasDrawingSession session, float width, float height)
    {
        var wave = _item!.Waveform;

        if (wave.Count == 0)
        {
            return;
        }

        var midY = height / 2f;
        var bars = Math.Min(wave.Count, (int)Math.Max(50, width / 2));
        var barWidth = width / bars;
        var step = wave.Count / (double)bars;
        var thickness = Math.Max(1f, barWidth - 1f);
        var color = Color.FromArgb(210, 200, 220, 255);

        for (var i = 0; i < bars; i++)
        {
            var amplitude = Math.Clamp(wave[Math.Min(wave.Count - 1, (int)(i * step))], 0f, 1f);
            var barHeight = Math.Max(1.5f, amplitude * (height - 8f));
            var x = (i * barWidth) + (barWidth / 2f);

            session.DrawLine(x, midY - (barHeight / 2f), x, midY + (barHeight / 2f), color, thickness);
        }
    }

    private void DrawSpectrogram(
        CanvasDrawingSession session, CanvasControl sender, SpectrogramData data, float width, float height)
    {
        // One BGRA pixel per cell, uploaded as a bitmap and stretched. Drawing a rectangle per cell
        // would be tens of thousands of draw calls for a picture the GPU can filter for free.
        var pixels = new byte[data.Columns * data.Bins * 4];

        for (var column = 0; column < data.Columns; column++)
        {
            for (var bin = 0; bin < data.Bins; bin++)
            {
                // Bin 0 is the lowest frequency and belongs at the bottom of the image.
                var row = data.Bins - 1 - bin;
                var offset = ((row * data.Columns) + column) * 4;
                var (r, g, b) = Ramp(data.At(column, bin));

                pixels[offset + 0] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        }

        using var bitmap = CanvasBitmap.CreateFromBytes(
            sender, pixels, data.Columns, data.Bins, DirectXPixelFormat.B8G8R8A8UIntNormalized);

        session.DrawImage(bitmap, new Rect(0, 0, width, height));
    }

    /// <summary>
    /// Magnitude to colour: near-black through blue and magenta to a hot yellow.
    /// <para>
    /// Monotonic in lightness, so it reads correctly in greyscale and does not invent a bright band
    /// in the middle of the range the way a rainbow ramp does.
    /// </para>
    /// </summary>
    private static (byte R, byte G, byte B) Ramp(float value)
    {
        value = Math.Clamp(value, 0f, 1f);

        var r = (byte)(255 * Math.Clamp(value * 2.2f - 0.35f, 0f, 1f));
        var g = (byte)(255 * Math.Clamp(value * 2.6f - 1.35f, 0f, 1f));
        var b = (byte)(255 * Math.Clamp(value * 3.0f - 0.05f, 0f, 1f) * (1f - Math.Clamp(value * 1.6f - 0.65f, 0f, 1f)));

        return (r, g, b);
    }

    private void EnsurePlayhead()
    {
        if (_playhead is not null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(PlayheadHost).Compositor;

        _playhead = compositor.CreateSpriteVisual();
        _playhead.Brush = compositor.CreateColorBrush(Color.FromArgb(255, 96, 165, 250));
        _playhead.Size = new Vector2(2f, 0f);
        _playhead.IsVisible = false;

        // The position arrives once per playback tick. Without an implicit animation the head
        // jumps between ticks; with one the compositor interpolates, so it stays smooth even while
        // the UI thread is busy rendering an export.
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertExpressionKeyFrame(1f, "this.FinalValue");
        slide.Duration = TimeSpan.FromMilliseconds(90);

        // Target is not optional. Registering an implicit animation under a property key does not
        // tell the animation what to animate; without this the first write to Offset below threw
        // "The triggered animation must have a target specified" out of a pointer event, which
        // ended the process. Since UpdatePlayhead only assigns Offset while a take is playing or
        // being scrubbed, that meant clicking a waveform - or playing any clip - killed the app.
        slide.Target = nameof(Visual.Offset);

        var implicits = compositor.CreateImplicitAnimationCollection();
        implicits[nameof(Visual.Offset)] = slide;
        _playhead.ImplicitAnimations = implicits;

        ElementCompositionPreview.SetElementChildVisual(PlayheadHost, _playhead);
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        EnsurePlayhead();

        if (_playhead is null)
        {
            return;
        }

        if (_item is null || (!_item.IsPlaying && !_isScrubbing))
        {
            _playhead.IsVisible = false;
            return;
        }

        var width = (float)RootGrid.ActualWidth;
        var height = (float)RootGrid.ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        _playhead.Size = new Vector2(2f, Math.Max(1f, height - FooterHeight));
        _playhead.Offset = new Vector3((float)(_item.PlayheadRatio * width), 0f, 0f);
        _playhead.IsVisible = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_item is null || _item.DurationSeconds <= 0)
        {
            return;
        }

        _isScrubbing = true;
        RootGrid.CapturePointer(e.Pointer);
        ReportScrub(e);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_item is null || _item.DurationSeconds <= 0)
        {
            return;
        }

        var position = e.GetCurrentPoint(RootGrid).Position;
        var ratio = Math.Clamp(position.X / Math.Max(1.0, RootGrid.ActualWidth), 0.0, 1.0);

        HoverTipText.Text = FormatTime(ratio * _item.DurationSeconds);
        HoverTip.Visibility = Visibility.Visible;
        HoverTip.Margin = new Thickness(Math.Max(0, position.X - 24), 6, 0, 0);

        if (_isScrubbing)
        {
            ReportScrub(e);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbing || _item is null)
        {
            return;
        }

        _isScrubbing = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        var seconds = SecondsAt(e);
        var matched = _item.Segments.FirstOrDefault(s => seconds >= s.StartSeconds && seconds <= s.EndSeconds);
        SegmentClicked?.Invoke(_item, matched);

        UpdatePlayhead();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        HoverTip.Visibility = Visibility.Collapsed;
    }

    private void ReportScrub(PointerRoutedEventArgs e)
    {
        var seconds = SecondsAt(e);
        _item!.PlayheadRatio = _item.DurationSeconds > 0 ? seconds / _item.DurationSeconds : 0;
        UpdatePlayhead();
        Scrubbed?.Invoke(_item, seconds);
    }

    private double SecondsAt(PointerRoutedEventArgs e)
    {
        var x = e.GetCurrentPoint(RootGrid).Position.X;
        var ratio = Math.Clamp(x / Math.Max(1.0, RootGrid.ActualWidth), 0.0, 1.0);
        return ratio * _item!.DurationSeconds;
    }

    private static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));

        return span.TotalMinutes >= 1
            ? $"{span.Minutes}:{span.Seconds:D2}.{span.Milliseconds / 100:D1}"
            : $"{span.Seconds}.{span.Milliseconds / 100:D1}s";
    }
}
