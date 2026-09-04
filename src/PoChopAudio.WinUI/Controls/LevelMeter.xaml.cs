using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PoChopAudio.WinUI.Controls;

public sealed partial class LevelMeter : UserControl
{
    public LevelMeter()
    {
        InitializeComponent();
    }

    public void UpdateLevel(double peakDb, double rmsDb, bool isClipping)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Map -60 dB .. 0 dB to 0 .. 100%
            var norm = Math.Clamp((peakDb + 60.0) / 60.0, 0.0, 1.0) * 100.0;
            LevelBar.Value = norm;

            if (peakDb <= -60)
            {
                DbText.Text = "-∞ dB";
            }
            else
            {
                DbText.Text = $"{peakDb:F1} dB";
            }

            // The bar is the only part of this control with a value, so it is what a screen reader
            // reads. Without a name it announces as an unlabelled progress bar at some percentage,
            // which says nothing about the level being recorded.
            AutomationProperties.SetName(LevelBar, $"Input level {DbText.Text}");

            if (isClipping)
            {
                ClipBadge.Visibility = Visibility.Visible;
                LevelBar.Foreground = new SolidColorBrush(Colors.Red);
            }
            else if (peakDb > -6)
            {
                ClipBadge.Visibility = Visibility.Collapsed;
                LevelBar.Foreground = new SolidColorBrush(Colors.Orange);
            }
            else
            {
                ClipBadge.Visibility = Visibility.Collapsed;
                LevelBar.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 34, 197, 94)); // Green
            }
        });
    }

    public void Reset()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            LevelBar.Value = 0;
            DbText.Text = "-∞ dB";
            AutomationProperties.SetName(LevelBar, "Input level, silent");
            ClipBadge.Visibility = Visibility.Collapsed;
        });
    }
}

