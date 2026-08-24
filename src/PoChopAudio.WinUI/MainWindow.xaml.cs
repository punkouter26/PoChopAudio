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
        this.TrySetMicaBackdrop();
        this.SetWindowSize(1200, 850);
    }

    private void OnNavViewLoaded(object sender, RoutedEventArgs e)
    {
        NavigateTo("Chop");
    }

    private void OnNavigationItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        Type pageType = tag switch
        {
            "Chop" => typeof(ChopPage),
            "Cutout" => typeof(CutoutPage),
            "HeadShots" => typeof(HeadShotsPage),
            "Health" => typeof(HealthPage),
            _ => typeof(ChopPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
