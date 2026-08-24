using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace PoChopAudio.WinUI.Common;

public static class WindowHelper
{
    public static nint GetHwnd(this Window window)
    {
        return WindowNative.GetWindowHandle(window);
    }

    public static void InitializeWithWindow(object target, Window window)
    {
        var hwnd = window.GetHwnd();
        InitializeWithWindow.Initialize(target, hwnd);
    }

    public static void SetWindowSize(this Window window, int width, int height)
    {
        var hwnd = window.GetHwnd();
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        if (appWindow is not null)
        {
            appWindow.Resize(new SizeInt32(width, height));
        }
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

