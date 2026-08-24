using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PoChopAudio.WinUI.ViewModels;

namespace PoChopAudio.WinUI.Views;

public sealed partial class HealthPage : Page
{
    public HealthViewModel ViewModel { get; }

    public HealthPage()
    {
        ViewModel = App.GetService<HealthViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshHealthAsync();
    }
}

