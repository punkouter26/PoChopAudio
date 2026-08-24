using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PoChopAudio.WinUI.Services;

public sealed class LocalCutoutService(CutoutApiClient cutoutClient) : IDisposable
{
    private InferenceSession? _session;
    private byte[]? _modelBytes;
    private readonly object _lock = new();

    public async Task<bool> EnsureModelLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_session is not null) return true;

        // Try local file next to app or in models directory
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Content", "Models", "u2netp.onnx"),
            Path.Combine(AppContext.BaseDirectory, "u2netp.onnx"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoChopAudio", "u2netp.onnx")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    _session = new InferenceSession(path);
                    return true;
                }
                catch
                {
                    // Continue to next candidate
                }
            }
        }

        // Try downloading from API server
        var modelBytes = await cutoutClient.DownloadModelAsync(cancellationToken);
        if (modelBytes is not null && modelBytes.Length > 0)
        {
            try
            {
                _modelBytes = modelBytes;
                _session = new InferenceSession(modelBytes);

                // Cache locally
                var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoChopAudio");
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllBytesAsync(Path.Combine(cacheDir, "u2netp.onnx"), modelBytes, cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public async Task<(byte[] CutoutPng, int Width, int Height)> ProcessHeadshotAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        var modelReady = await EnsureModelLoadedAsync(cancellationToken);
        if (!modelReady || _session is null)
        {
            throw new InvalidOperationException("ONNX model u2netp.onnx is not loaded.");
        }

        return await Task.Run(() =>
        {
            using var image = Image.Load<Rgba32>(imageBytes);
            var origWidth = image.Width;
            var origHeight = image.Height;

            // Prepare 320x320 input tensor
            const int inputSize = 320;
            using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(inputSize, inputSize),
                Mode = ResizeMode.Stretch
            }));

            var tensor = new DenseTensor<float>([1, 3, inputSize, inputSize]);
            float[] mean = [0.485f, 0.456f, 0.406f];
            float[] std = [0.229f, 0.224f, 0.225f];

            for (int y = 0; y < inputSize; y++)
            {
                for (int x = 0; x < inputSize; x++)
                {
                    var pixel = resized[x, y];
                    tensor[0, 0, y, x] = ((pixel.R / 255f) - mean[0]) / std[0];
                    tensor[0, 1, y, x] = ((pixel.G / 255f) - mean[1]) / std[1];
                    tensor[0, 2, y, x] = ((pixel.B / 255f) - mean[2]) / std[2];
                }
            }

            var inputName = _session.InputMetadata.Keys.First();
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

            using var results = _session.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();

            // Find min/max for normalization
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;
            for (int y = 0; y < inputSize; y++)
            {
                for (int x = 0; x < inputSize; x++)
                {
                    var val = outputTensor[0, 0, y, x];
                    if (val < minVal) minVal = val;
                    if (val > maxVal) maxVal = val;
                }
            }

            var range = maxVal - minVal;
            if (range <= 0.00001f) range = 1f;

            // Generate mask image
            using var mask = new Image<L8>(inputSize, inputSize);
            for (int y = 0; y < inputSize; y++)
            {
                for (int x = 0; x < inputSize; x++)
                {
                    var normalized = (outputTensor[0, 0, y, x] - minVal) / range;
                    byte alpha = (byte)Math.Clamp((int)(normalized * 255f), 0, 255);
                    mask[x, y] = new L8(alpha);
                }
            }

            // Upscale mask to original size
            mask.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(origWidth, origHeight),
                Mode = ResizeMode.Stretch
            }));

            // Apply mask to original image
            int minX = origWidth, minY = origHeight, maxX = 0, maxY = 0;
            for (int y = 0; y < origHeight; y++)
            {
                for (int x = 0; x < origWidth; x++)
                {
                    var p = image[x, y];
                    var alpha = mask[x, y].PackedValue;
                    if (alpha > 20)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        image[x, y] = new Rgba32(p.R, p.G, p.B, alpha);
                    }
                    else
                    {
                        image[x, y] = new Rgba32(0, 0, 0, 0);
                    }
                }
            }

            // Crop to tight bounding box with slight padding
            if (maxX > minX && maxY > minY)
            {
                int pad = 16;
                int cropX = Math.Max(0, minX - pad);
                int cropY = Math.Max(0, minY - pad);
                int cropW = Math.Min(origWidth - cropX, (maxX - minX) + (pad * 2));
                int cropH = Math.Min(origHeight - cropY, (maxY - minY) + (pad * 2));

                image.Mutate(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH)));
            }

            using var outStream = new MemoryStream();
            image.SaveAsPng(outStream);
            return (outStream.ToArray(), image.Width, image.Height);
        }, cancellationToken);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}

