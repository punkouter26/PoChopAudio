using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Services.Cutout.Engines;
using PoChopAudio.Shared;
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

        // Without a provider every log call was discarded, which left the image pipeline with no
        // way to explain itself when a result looked wrong. The provider is registered as well as
        // added, because the Settings page shows where it writes and offers to open the folder.
        var logProvider = new FileLoggerProvider();
        services.AddSingleton(logProvider);
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(logProvider));

        // The only state that outlives a run. Registered first so anything below could read it.
        services.AddSingleton<AppSettingsService>();

        // The shared engine room, registered exactly as the API registers it. Running the same
        // ChopService and CutoutService in-process is what makes this app work with no server.
        services.AddSingleton<ChopJobStore>();
        services.AddSingleton<ChopService>();

        services.AddSingleton(new CutoutModelOptions(
            Path.Combine(AppContext.BaseDirectory, "Content", "Models", "u2netp.onnx")));
        services.AddSingleton<IBackgroundRemover, OnnxU2NetRemover>();
        services.AddSingleton<EnginePicker>();

        // Face detection is a Windows API, so Services declares the interface and the app supplies
        // the implementation. It degrades on its own if the OS component is missing.
        services.AddSingleton<IFaceLocator, WindowsFaceLocator>();
        services.AddSingleton<CutoutService>();

        // Hardware / Media Services
        services.AddSingleton<AudioRecorderService>();
        services.AddSingleton<AudioPlayerService>();
        services.AddSingleton<CameraService>();
        services.AddSingleton<ExportService>();

        // ViewModels
        services.AddTransient<ChopViewModel>();
        services.AddTransient<CutoutViewModel>();
        services.AddTransient<SettingsViewModel>();

        _serviceProvider = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}

