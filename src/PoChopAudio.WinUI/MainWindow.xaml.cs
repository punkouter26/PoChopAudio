using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.WinUI.Common;
using PoChopAudio.WinUI.Views;

namespace PoChopAudio.WinUI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        WindowHelper.TrySetMicaBackdrop(this);
        WindowHelper.SetWindowSize(this, 1280, 840);

        // Open on Cutout Studio. ChopPage is still excluded from compilation, so selecting the
        // first item landed the user on the Health fallback instead of anything they asked for.
        SelectNavItem("Cutout");
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void SelectNavItem(string tag)
    {
        var item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (i.Tag as string) == tag);

        if (item is not null)
        {
            NavView.SelectedItem = item;
        }

        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        // Cutout Studio is the only page that exists; ChopPage is still excluded from the build.
        Type pageType = typeof(CutoutPage);
        _ = tag;

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
