using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using PoChopAudio.WinUI.Services;
using PoChopAudio.WinUI.ViewModels;

namespace PoChopAudio.WinUI;

public partial class App : Application
{
    private static IServiceProvider? _serviceProvider;
    private static MainWindow? _mainWindow;

    public static MainWindow MainWindow => _mainWindow ?? throw new InvalidOperationException("Main window not initialized.");

    public static T GetService<T>() where T : class
    {
        return _serviceProvider?.GetRequiredService<T>()
            ?? throw new InvalidOperationException($"Service {typeof(T).Name} is not registered.");
    }

    public App()
    {
        InitializeComponent();
        ConfigureServices();
    }

    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // API & Core Services
        services.AddSingleton<IApiConfiguration, ApiConfiguration>();
        services.AddSingleton<ChopApiClient>();
        services.AddSingleton<CutoutApiClient>();
        services.AddSingleton<DiagnosticsApiClient>();

        // Hardware / Media Services
        services.AddSingleton<AudioRecorderService>();
        services.AddSingleton<AudioPlayerService>();
        services.AddSingleton<CameraService>();
        services.AddSingleton<LocalCutoutService>();
        services.AddSingleton<ExportService>();

        // ViewModels
        services.AddTransient<ChopViewModel>();
        services.AddTransient<CutoutViewModel>();
        services.AddTransient<HeadShotsViewModel>();
        services.AddTransient<HealthViewModel>();

        _serviceProvider = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}

