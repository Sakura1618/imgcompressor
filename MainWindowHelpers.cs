using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace imgcompressor
{
    internal static class MainWindowHelpers
    {

        private static async Task CompressWithFormatAsync(string inputPath, string outputPath, string format, bool isLossy, int maxColors, int targetMb, int maxIter, int colorStep, CancellationToken token, Action<int, int>? iterationCallback)
        {
            token.ThrowIfCancellationRequested();
            using var original = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(inputPath, token);

            if (format == "PNG")
            {
                await MainWindow.CompressPngAsync(inputPath, original, outputPath, isLossy, maxColors, targetMb, maxIter, colorStep, token, iterationCallback);
                return;
            }
            else if (format == "JPEG")
            {
                var qualityFromTarget = MainWindow.ComputeQuality(targetMb);
                var encoder = new JpegEncoder { Quality = qualityFromTarget };
                token.ThrowIfCancellationRequested();
                await original.SaveAsJpegAsync(outputPath, encoder, token);
                iterationCallback?.Invoke(1, 1);
                return;
            }
            else if (format == "WebP")
            {
                var qualityFromTarget = MainWindow.ComputeQuality(targetMb);
                var encoder = new WebpEncoder { Quality = qualityFromTarget };
                token.ThrowIfCancellationRequested();
                await original.SaveAsWebpAsync(outputPath, encoder, token);
                iterationCallback?.Invoke(1, 1);
                return;
            }

            await MainWindow.CompressPngAsync(inputPath, original, outputPath, isLossy, maxColors, targetMb, maxIter, colorStep, token, iterationCallback);
        }
    }
}