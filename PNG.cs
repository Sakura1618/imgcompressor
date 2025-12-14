using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace imgcompressor
{
    public class HighCompressionPNG
    {
        public class CompressionResult
        {
            public required string FilePath { get; set; } = string.Empty;
            public long OriginalSize { get; set; }
            public long CompressedSize { get; set; }
            public double CompressionRatio { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public static CompressionResult CompressLargePNG(string inputPath, string outputPath, int targetSizeMB = 10)
        {
            var originalInfo = new FileInfo(inputPath);
            var originalSizeMB = originalInfo.Length / (1024.0 * 1024.0);

            using var image = Image.Load<Rgba32>(inputPath);
            int originalWidth = image.Width;
            int originalHeight = image.Height;

            double scaleFactor = CalculateOptimalScale(originalSizeMB, targetSizeMB);
            (int newWidth, int newHeight) = CalculateOptimalDimensions(image.Width, image.Height, scaleFactor, targetSizeMB);

            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(newWidth, newHeight),
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Max,
                Compand = true
            }));

            long origEstimateBytes = (long)originalWidth * originalHeight * 4;

            int optimalColors = CalculateOptimalColors(newWidth, newHeight, targetSizeMB);

            var encoder = CreateOptimizedEncoder(optimalColors, originalSizeMB, targetSizeMB);

            byte[] bestBytes;
            using (var tmpMs = new MemoryStream())
            {
                image.Save(tmpMs, encoder);
                bestBytes = tmpMs.ToArray();
            }

            var compressedSizeMB = bestBytes.Length / (1024.0 * 1024.0);

            if (compressedSizeMB < targetSizeMB * 0.6 && optimalColors < 512)
            {
                int low = optimalColors;
                int high = 512;
                double bestDiff = Math.Abs(compressedSizeMB - targetSizeMB);
                byte[]? bestCandidate = bestBytes;
                int iterations = 0;
                while (low <= high && iterations < 10)
                {
                    int mid = (low + high) / 2;
                    var tryEncoder = CreateOptimizedEncoder(mid, originalSizeMB, targetSizeMB);
                    using var msTry = new MemoryStream();
                    image.Save(msTry, tryEncoder);
                    var trySizeMB = msTry.Length / (1024.0 * 1024.0);
                    var diff = Math.Abs(trySizeMB - targetSizeMB);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestCandidate = msTry.ToArray();
                    }

                    if (trySizeMB < targetSizeMB) low = mid + 1; else high = mid - 1;
                    iterations++;
                }

                if (bestCandidate != null)
                {
                    bestBytes = bestCandidate;
                    compressedSizeMB = bestBytes.Length / (1024.0 * 1024.0);
                }
            }

            if (compressedSizeMB > targetSizeMB * 1.05)
            {
                using var orig = Image.Load<Rgba32>(inputPath);
                double lowScale = 0.3;
                double highScale = Math.Min(1.0, Math.Max(0.99, (double)bestBytes.Length / (origEstimateBytes)));
                highScale = Math.Min(1.0, Math.Max(highScale, 0.99));

                double closestDiff = double.MaxValue;
                byte[]? bestScaled = null;

                for (int i = 0; i < 10; i++)
                {
                    double mid = (lowScale + highScale) / 2.0;
                    int w = Math.Max(2, (int)(orig.Width * mid));
                    int h = Math.Max(2, (int)(orig.Height * mid));
                    w -= w % 2; h -= h % 2;

                    using var tmp = orig.Clone();
                    tmp.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(w, h), Sampler = KnownResamplers.Lanczos3, Mode = ResizeMode.Max }));
                    int colorsForSize = CalculateOptimalColors(w, h, targetSizeMB);
                    var enc = CreateOptimizedEncoder(colorsForSize, originalSizeMB, targetSizeMB);
                    using var msTry = new MemoryStream();
                    tmp.Save(msTry, enc);
                    var trySizeMB = msTry.Length / (1024.0 * 1024.0);
                    var diff = Math.Abs(trySizeMB - targetSizeMB);
                    if (diff < closestDiff)
                    {
                        closestDiff = diff;
                        bestScaled = msTry.ToArray();
                    }

                    if (trySizeMB > targetSizeMB) highScale = mid; else lowScale = mid;
                }

                if (bestScaled != null)
                {
                    var finalSizeMB = bestScaled.Length / (1024.0 * 1024.0);
                    if (finalSizeMB <= targetSizeMB * 1.2)
                    {
                        File.WriteAllBytes(outputPath, bestScaled);
                        return new CompressionResult
                        {
                            FilePath = outputPath,
                            OriginalSize = originalInfo.Length,
                            CompressedSize = bestScaled.Length,
                            CompressionRatio = finalSizeMB / originalSizeMB,
                            Width = (int)(orig.Width * lowScale),
                            Height = (int)(orig.Height * lowScale)
                        };
                    }
                }

                var secondary = ApplySecondaryCompression(outputPath, outputPath + ".opt.png", targetSizeMB);
                return secondary;
            }

            File.WriteAllBytes(outputPath, bestBytes);

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSize = originalInfo.Length,
                CompressedSize = bestBytes.Length,
                CompressionRatio = compressedSizeMB / originalSizeMB,
                Width = newWidth,
                Height = newHeight
            };
        }

        private static double CalculateOptimalScale(double originalSizeMB, int targetSizeMB)
        {
            double sizeRatio = targetSizeMB / Math.Max(1e-6, originalSizeMB);
            double scaleEstimate = Math.Sqrt(sizeRatio) * 0.95;
            return Math.Clamp(scaleEstimate, 0.3, 1.0);
        }

        private static (int width, int height) CalculateOptimalDimensions(int originalWidth, int originalHeight, double scaleFactor, int targetSizeMB)
        {
            int newWidth = Math.Max(2, (int)Math.Round(originalWidth * scaleFactor));
            int newHeight = Math.Max(2, (int)Math.Round(originalHeight * scaleFactor));
            if ((newWidth & 1) == 1) newWidth--;
            if ((newHeight & 1) == 1) newHeight--;
            int minDimension = targetSizeMB < 5 ? 1200 : 2000;
            newWidth = Math.Max(minDimension, newWidth);
            newHeight = Math.Max(minDimension, newHeight);
            return (newWidth, newHeight);
        }

        private static int CalculateOptimalColors(int width, int height, int targetSizeMB)
        {
            long totalPixels = (long)width * height;
            return totalPixels > 8_000_000 ? 128 : totalPixels > 4_000_000 ? 256 : 512;
        }

        private static PngEncoder CreateOptimizedEncoder(int colors, double originalSizeMB, int targetSizeMB)
        {
            bool usePalette = colors <= 256;
            var compressionLevel = originalSizeMB > 20 ? PngCompressionLevel.BestCompression : PngCompressionLevel.DefaultCompression;

            var encoder = new PngEncoder
            {
                CompressionLevel = compressionLevel,
                ColorType = usePalette ? PngColorType.Palette : PngColorType.RgbWithAlpha,
                BitDepth = PngBitDepth.Bit8,
                FilterMethod = PngFilterMethod.Adaptive,
                InterlaceMethod = PngInterlaceMode.None,
                Quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = colors, Dither = colors <= 128 ? KnownDitherings.FloydSteinberg : null })
            };

            return encoder;
        }

        private static CompressionResult ApplySecondaryCompression(string inputPath, string outputPath, int targetSizeMB)
        {
            using var image = Image.Load(inputPath);
            var aggressiveEncoder = new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestCompression,
                ColorType = PngColorType.Palette,
                BitDepth = PngBitDepth.Bit4,
                FilterMethod = PngFilterMethod.None,
                InterlaceMethod = PngInterlaceMode.None,
                Quantizer = new OctreeQuantizer(new QuantizerOptions { MaxColors = 16, Dither = KnownDitherings.FloydSteinberg })
            };

            image.Save(outputPath, aggressiveEncoder);

            var resultInfo = new FileInfo(outputPath);
            var compressedSizeMB = resultInfo.Length / (1024.0 * 1024.0);

            return new CompressionResult
            {
                FilePath = outputPath,
                OriginalSize = new FileInfo(inputPath).Length,
                CompressedSize = resultInfo.Length,
                CompressionRatio = compressedSizeMB / targetSizeMB,
                Width = image.Width,
                Height = image.Height
            };
        }
    }
}