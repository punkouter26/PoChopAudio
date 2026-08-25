using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PoChopAudio.WinUI.Common;

public static class WindowHelper
{
    public static nint GetHwnd(this Window window)
    {
        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    public static void InitWithWindow(object target, Window window)
    {
        var hwnd = window.GetHwnd();
        WinRT.Interop.InitializeWithWindow.Initialize(target, hwnd);
    }

    /// <summary>
    /// Sizes the window to a fraction of the monitor's work area and centres it.
    /// <para>
    /// Deliberately does no DPI arithmetic. The previous fixed <c>Resize(1280, 840)</c> opened the
    /// app at roughly 853x560 effective pixels on a 150% display -- far too narrow for the Chop
    /// page, whose right-hand controls were clipped as a result. Correcting that with
    /// <c>GetDpiForWindow</c> is not reliable: during window construction it can report the wrong
    /// scale, because the window has not yet been shown on its target monitor, and the measured
    /// results were not reproducible between runs. <c>DisplayArea.WorkArea</c> and
    /// <c>AppWindow.Resize</c> share a coordinate space, so deriving the size from the work area
    /// needs no conversion and stays correct at any scale factor.
    /// </para>
    /// </summary>
    /// <param name="widthFraction">Share of the work area's width to occupy, 0-1.</param>
    /// <param name="heightFraction">Share of the work area's height to occupy, 0-1.</param>
    public static void SetWindowSizeToWorkAreaFraction(
        this Window window, double widthFraction, double heightFraction)
    {
        var hwnd = window.GetHwnd();
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        if (appWindow is null)
        {
            return;
        }

        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        if (area is null)
        {
            return;
        }

        var work = area.WorkArea;
        var width = (int)(work.Width * Math.Clamp(widthFraction, 0.1, 1.0));
        var height = (int)(work.Height * Math.Clamp(heightFraction, 0.1, 1.0));

        appWindow.MoveAndResize(new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
    }

    public static bool TrySetMicaBackdrop(this Window window)
    {
        if (MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop();
            return true;
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return true;
        }
        return false;
    }
}

