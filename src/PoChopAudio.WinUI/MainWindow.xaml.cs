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
        WindowHelper.SetWindowSizeToWorkAreaFraction(this, 0.82, 0.88);

        SelectNavItem("Chop");
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
        Type pageType = tag switch
        {
            "Cutout" => typeof(CutoutPage),
            _ => typeof(ChopPage),
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
