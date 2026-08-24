using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoChopAudio.Shared;
using PoChopAudio.WinUI.Services;

namespace PoChopAudio.WinUI.ViewModels;

public partial class HealthViewModel : ObservableObject
{
    private readonly IApiConfiguration _config;
    private readonly DiagnosticsApiClient _diagClient;
    private readonly ChopApiClient _chopClient;
    private readonly CutoutApiClient _cutoutClient;

    public HealthViewModel(
        IApiConfiguration config,
        DiagnosticsApiClient diagClient,
        ChopApiClient chopClient,
        CutoutApiClient cutoutClient)
    {
        _config = config;
        _diagClient = diagClient;
        _chopClient = chopClient;
        _cutoutClient = cutoutClient;

        _serverUrl = _config.BaseUrl;
    }

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private bool _isHealthy;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private string _statusText = "Not checked";

    [ObservableProperty]
    private string _diagnosticsJson = string.Empty;

    [ObservableProperty]
    private ChopCapabilities? _chopCaps;

    [ObservableProperty]
    private CutoutCapabilities? _cutoutCaps;

    [RelayCommand]
    public async Task RefreshHealthAsync()
    {
        IsChecking = true;
        StatusText = "Connecting to API server…";

        try
        {
            _config.BaseUrl = ServerUrl.Trim();
            IsHealthy = await _diagClient.CheckHealthAsync();

            if (IsHealthy)
            {
                StatusText = "Connected & Healthy";
                DiagnosticsJson = await _diagClient.GetDiagJsonAsync() ?? "{}";
                ChopCaps = await _chopClient.GetCapabilitiesAsync();
                CutoutCaps = await _cutoutClient.GetCapabilitiesAsync();
            }
            else
            {
                StatusText = $"Server at {ServerUrl} is not responding (make sure './SCRIPTS/setup.ps1 -Run' is started).";
                DiagnosticsJson = string.Empty;
                ChopCaps = null;
                CutoutCaps = null;
            }
        }
        catch (Exception ex)
        {
            IsHealthy = false;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }
}

