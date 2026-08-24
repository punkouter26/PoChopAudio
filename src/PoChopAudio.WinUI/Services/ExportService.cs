using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using PoChopAudio.WinUI.Common;

namespace PoChopAudio.WinUI.Services;

public sealed class ExportService
{
    public static async Task<string?> PickFolderAsync(Window window)
    {
        var picker = new FolderPicker();
        WindowHelper.InitializeWithWindow(picker, window);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(Window window, string defaultName, string extension, string fileTypeDesc)
    {
        var picker = new FileSavePicker();
        WindowHelper.InitializeWithWindow(picker, window);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.SuggestedFileName = defaultName;
        picker.FileTypeChoices.Add(fileTypeDesc, [extension]);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public static async Task SaveStreamToFileAsync(Stream sourceStream, string destinationPath, CancellationToken cancellationToken = default)
    {
        await using var targetStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(targetStream, cancellationToken);
    }

    public static async Task SaveBytesToFileAsync(byte[] bytes, string destinationPath, CancellationToken cancellationToken = default)
    {
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken);
    }
}

