using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoChopAudio.Services.Chop;
using PoChopAudio.Services.Cutout;
using PoChopAudio.Shared;

namespace PoChopAudio.WinUI.ViewModels;

/// <summary>
/// Reports what this build can actually do. There is no server to reach any more, so the checks
/// are local: which codecs the platform gave us, and whether the ONNX model shipped alongside the
/// executable. Both follow the optional-capability pattern — absent means degraded, never broken.
/// </summary>
public partial class HealthViewModel(
    ChopService chop,
    CutoutService cutout,
    CutoutModelOptions model) : ObservableObject
{
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
    public Task RefreshHealthAsync()
    {
        IsChecking = true;

        try
        {
            ChopCaps = chop.GetCapabilities();
            CutoutCaps = cutout.GetCapabilities();

            var modelPresent = File.Exists(model.ModelPath);
            var engineCount = CutoutCaps.AvailableEngines.Count;

            // Audio always works; cutout needs the model. Neither can be "unreachable" now.
            IsHealthy = true;
            StatusText = engineCount > 0
                ? "Running locally — audio and cutout both ready"
                : "Running locally — audio ready, cutout unavailable (u2netp.onnx is missing)";

            DiagnosticsJson = JsonSerializer.Serialize(
                new
                {
                    mode = "in-process, no server",
                    platform = Environment.OSVersion.VersionString,
                    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    audioCodecs = ChopCaps.SupportedExtensions,
                    imageFormats = CutoutCaps.SupportedExtensions,
                    cutoutEngines = CutoutCaps.AvailableEngines.Select(e => e.ToString()).ToArray(),
                    modelPath = model.ModelPath,
                    modelPresent,
                },
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception exception)
        {
            IsHealthy = false;
            StatusText = $"Error: {exception.Message}";
            DiagnosticsJson = string.Empty;
        }
        finally
        {
            IsChecking = false;
        }

        return Task.CompletedTask;
    }
}
