using Microsoft.Win32;
using Ookii.Dialogs.Wpf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;

namespace imgcompressor
{
    public partial class MainWindow : Window
    {
        private readonly List<string> inputFiles = new List<string>();
        private string? outputFolder;
        private int completedCount;
        private CancellationTokenSource? cts;

        public MainWindow()
        {
            InitializeComponent();
            ModeCombo.SelectedIndex = 0;
            FormatCombo.SelectedIndex = 0;

            if (TargetSizeSlider != null) TargetSizeSlider.ValueChanged += TargetSizeSlider_ValueChanged;
            if (ConcurrencySlider != null) ConcurrencySlider.ValueChanged += ConcurrencySlider_ValueChanged;
            if (MaxTempSlider != null) MaxTempSlider.ValueChanged += MaxTempSlider_ValueChanged;
            if (FormatCombo != null) FormatCombo.SelectionChanged += FormatCombo_SelectionChanged;
            if (AdvancedToggle != null)
            {
                AdvancedToggle.Checked += AdvancedToggle_Checked;
                AdvancedToggle.Unchecked += AdvancedToggle_Unchecked;
            }

            UpdateControlsVisibility();

            // ensure OpenFolderButton initial state
            if (OpenFolderButton != null) OpenFolderButton.IsEnabled = false;
        }

        private void AdvancedToggle_Checked(object? _sender, RoutedEventArgs _e) => AnimatePanel(AdvancedPanel, true);
        private void AdvancedToggle_Unchecked(object? _sender, RoutedEventArgs _e) => AnimatePanel(AdvancedPanel, false);

        private void AnimatePanel(FrameworkElement? panel, bool show)
        {
            if (panel == null) return;
            panel.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = show ? 0 : 1,
                To = show ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(250)
            };
            anim.Completed += (s, e) => { if (!show) panel.Visibility = Visibility.Collapsed; };
            panel.BeginAnimation(OpacityProperty, anim);
        }

        private void UpdateControlsVisibility()
        {
            if (ModeCombo == null || FormatCombo == null) return;

            // Hide the target size controls for lossless mode (index 0), show for lossy mode (index 1)
            if (ModeCombo.SelectedIndex == 0)
            {
                AnimatePanel(TargetPanel, false);
            }
            else
            {
                AnimatePanel(TargetPanel, true);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                AddFiles(files);
            }
        }

        private void AddFiles_Click(object? _sender, RoutedEventArgs _e)
        {
            var dlg = new OpenFileDialog { Filter = "PNG Files (*.png)|*.png", Multiselect = true };
            if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
        }

        private void AddFiles(string[] files)
        {
            foreach (var f in files)
            {
                if (string.Equals(Path.GetExtension(f), ".png", StringComparison.OrdinalIgnoreCase) && !inputFiles.Contains(f))
                {
                    inputFiles.Add(f);
                    FileList.Items.Add(Path.GetFileName(f));
                }
            }
        }

        private void RemoveFile_Click(object sender, RoutedEventArgs _e)
        {
            if (sender is Button button && button.Tag is string fileName)
            {
                var fullPath = inputFiles.Find(f => Path.GetFileName(f) == fileName);
                if (fullPath != null)
                {
                    inputFiles.Remove(fullPath);
                    FileList.Items.Remove(fileName);
                }
            }
        }

        private void SelectOutputFolder_Click(object? _sender, RoutedEventArgs _e)
        {
            var dialog = new VistaFolderBrowserDialog();
            if (dialog.ShowDialog() == true)
            {
                outputFolder = dialog.SelectedPath;
                OutputPathText.Text = outputFolder;
                if (OpenFolderButton != null) OpenFolderButton.IsEnabled = true;
            }
        }

        private void OpenOutputFolder_Click(object? _sender, RoutedEventArgs _e)
        {
            try
            {
                if (!string.IsNullOrEmpty(outputFolder) && Directory.Exists(outputFolder))
                {
                    Process.Start(new ProcessStartInfo { FileName = outputFolder, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                UiShowMessage($"无法打开文件夹: {ex.Message}");
            }
        }

        private void ModeCombo_SelectionChanged(object? _sender, SelectionChangedEventArgs _e) => UpdateControlsVisibility();

        private void TargetSizeSlider_ValueChanged(object? _sender, RoutedPropertyChangedEventArgs<double> _e)
        {
            if (TargetSizeValue != null) TargetSizeValue.Text = ((int)TargetSizeSlider.Value).ToString();
        }

        private void ConcurrencySlider_ValueChanged(object? _sender, RoutedPropertyChangedEventArgs<double> _e)
        {
            if (ConcurrencyValue != null) ConcurrencyValue.Text = ((int)ConcurrencySlider.Value).ToString();
        }

        private void MaxTempSlider_ValueChanged(object? _sender, RoutedPropertyChangedEventArgs<double> _e)
        {
            if (MaxTempValue != null) MaxTempValue.Text = ((int)MaxTempSlider.Value).ToString();
        }

        private void FormatCombo_SelectionChanged(object? _sender, SelectionChangedEventArgs _e) => UpdateControlsVisibility();

        private async void StartCompress_Click(object? _sender, RoutedEventArgs _e)
        {
            if (inputFiles.Count == 0 || string.IsNullOrEmpty(outputFolder))
            {
                MessageBox.Show("请添加文件并选择输出文件夹！");
                return;
            }

            var concurrency = (int)(ConcurrencySlider?.Value ?? 4);
            var maxTempMb = (int)(MaxTempSlider?.Value ?? 500);
            var maxTempBytes = (long)maxTempMb * 1024 * 1024;

            ProgressBar.Maximum = inputFiles.Count;
            ProgressBar.Value = 0;
            completedCount = 0;
            UiSetStatus("正在压缩...");
            UiEnableStart(false);
            UiEnableStop(true);

            var isLossy = ModeCombo.SelectedIndex == 1;
            var targetMb = (int)(TargetSizeSlider?.Value ?? 10);
            const int maxIter = 10;
            const int colors = 256;
            var format = (FormatCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "PNG";

            var semaphore = new SemaphoreSlim(concurrency);
            var tempUsage = 0L;
            var tempUsageLock = new object();
            cts = new CancellationTokenSource();

            var tasks = new List<Task>();

            foreach (var file in inputFiles.ToArray())
            {
                if (cts.IsCancellationRequested) break;
                var outName = Path.GetFileNameWithoutExtension(file) + (format == "PNG" ? ".png" : format == "JPEG" ? ".jpg" : ".webp");
                var outFile = Path.Combine(outputFolder, outName);

                await semaphore.WaitAsync();

                var token = cts.Token;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        while (true)
                        {
                            lock (tempUsageLock)
                            {
                                if (tempUsage < maxTempBytes)
                                {
                                    tempUsage += 10 * 1024 * 1024;
                                    break;
                                }
                            }
                            await Task.Delay(200, token);
                        }

                        token.ThrowIfCancellationRequested();
                        await CompressWithFormatAsync(file, outFile, format, isLossy, colors, targetMb, maxIter, 8, token, (iter, total) =>
                        {
                            UiUpdateProgress(completedCount + (double)iter / Math.Max(1, total), $"{(int)Math.Round(completedCount + (double)iter / Math.Max(1, total))}/{inputFiles.Count} ({format}) iter {iter}/{total}");
                        });
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException) { }
                        else { UiShowMessage($"处理 {file} 时出错: {ex.Message}"); }
                    }
                    finally
                    {
                        lock (tempUsageLock)
                        {
                            tempUsage = Math.Max(0, tempUsage - 10 * 1024 * 1024);
                        }

                        Interlocked.Increment(ref completedCount);
                        UiUpdateProgress(completedCount, $"{completedCount}/{inputFiles.Count}");

                        semaphore.Release();
                    }
                }, cts.Token));
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                UiSetStatus("已取消");
            }

            UiSetStatus("压缩完成！");
            UiEnableStart(true);
            UiEnableStop(false);
            FileList.Items.Clear();
            inputFiles.Clear();
            cts = null;
        }

        private void StopCompress_Click(object? _sender, RoutedEventArgs _e)
        {
            if (cts != null && !cts.IsCancellationRequested)
            {
                cts.Cancel();
                UiEnableStop(false);
            }
        }

        private static async Task CompressWithFormatAsync(string inputPath, string outputPath, string format, bool isLossy, int maxColors, int targetMb, int maxIter, int colorStep, CancellationToken token, Action<int, int>? iterationCallback)
        {
            token.ThrowIfCancellationRequested();
            using var original = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(inputPath, token);

            if (format == "PNG")
            {
                await CompressPngAsync(inputPath, original, outputPath, isLossy, maxColors, targetMb, maxIter, colorStep, token, iterationCallback);
                return;
            }
            else if (format == "JPEG")
            {
                var qualityFromTarget = ComputeQuality(targetMb);
                var encoder = new JpegEncoder { Quality = qualityFromTarget };
                token.ThrowIfCancellationRequested();
                await original.SaveAsJpegAsync(outputPath, encoder, token);
                iterationCallback?.Invoke(1, 1);
                return;
            }
            else if (format == "WebP")
            {
                var qualityFromTarget = ComputeQuality(targetMb);
                var encoder = new WebpEncoder { Quality = qualityFromTarget };
                token.ThrowIfCancellationRequested();
                await original.SaveAsWebpAsync(outputPath, encoder, token);
                iterationCallback?.Invoke(1, 1);
                return;
            }

            await CompressPngAsync(inputPath, original, outputPath, isLossy, maxColors, targetMb, maxIter, colorStep, token, iterationCallback);
        }

        internal static int ComputeQuality(int targetMb)
        {
            var quality = Math.Clamp(75 - (targetMb - 10) * 5, 10, 90);
            return quality;
        }

        internal static async Task CompressPngAsync(string inputPath, Image<Rgba32> original, string outputPath, bool isLossy, int maxColors, int targetMb, int maxIter, int colorStep, CancellationToken token, Action<int, int>? iterationCallback)
        {
            if (!isLossy)
            {
                var encoder = new PngEncoder
                {
                    CompressionLevel = PngCompressionLevel.BestCompression,
                    ColorType = PngColorType.RgbWithAlpha,
                    FilterMethod = PngFilterMethod.Adaptive,
                    TransparentColorMode = PngTransparentColorMode.Preserve,
                    SkipMetadata = true
                };

                token.ThrowIfCancellationRequested();
                await original.SaveAsPngAsync(outputPath, encoder, token);
                return;
            }

            var originalInfo = new FileInfo(inputPath);
            var originalSizeMB = originalInfo.Length / (1024.0 * 1024.0);

            double scaleFactorBest = CalculateOptimalScale(originalSizeMB, targetMb);
            var (bestW, bestH) = CalculateOptimalDimensions(original.Width, original.Height, scaleFactorBest, targetMb);

            using var copy = original.Clone();
            if (scaleFactorBest < 0.999)
            {
                copy.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions { Size = new SixLabors.ImageSharp.Size(bestW, bestH), Sampler = KnownResamplers.Lanczos3, Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max, Compand = true }));
            }

            int optimalColors = CalculateOptimalColors(bestW, bestH, targetMb);
            var optEncoder = CreateOptimizedEncoder(optimalColors, originalSizeMB, targetMb);

            token.ThrowIfCancellationRequested();
            await using var ms = new MemoryStream();
            await copy.SaveAsPngAsync(ms, optEncoder, token);
            var compressedSizeMB = ms.Length / (1024.0 * 1024.0);

            if (compressedSizeMB <= targetMb)
            {
                await File.WriteAllBytesAsync(outputPath, ms.ToArray());
                return;
            }

            var aggressiveEncoder = new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestCompression,
                ColorType = PngColorType.Palette,
                BitDepth = PngBitDepth.Bit4,
                FilterMethod = PngFilterMethod.None,
                InterlaceMethod = PngInterlaceMode.None,
                Quantizer = new SixLabors.ImageSharp.Processing.Processors.Quantization.OctreeQuantizer(new QuantizerOptions { MaxColors = 16, Dither = KnownDitherings.FloydSteinberg })
            };

            token.ThrowIfCancellationRequested();
            await using var ms2 = new MemoryStream();
            copy.Save(ms2, aggressiveEncoder);
            await File.WriteAllBytesAsync(outputPath, ms2.ToArray());
        }

        private static double CalculateOptimalScale(double originalSizeMB, int targetSizeMB)
        {
            double sizeRatio = targetSizeMB / Math.Max(0.0001, originalSizeMB);
            double scaleEstimate = Math.Sqrt(sizeRatio) * 0.9;
            double minScale = Math.Max(0.3, scaleEstimate);
            return Math.Min(1.0, minScale);
        }

        private static (int width, int height) CalculateOptimalDimensions(int originalWidth, int originalHeight, double scaleFactor, int targetSizeMB)
        {
            int newWidth = (int)(originalWidth * scaleFactor);
            int newHeight = (int)(originalHeight * scaleFactor);
            newWidth = newWidth - (newWidth % 2);
            newHeight = newHeight - (newHeight % 2);
            int minDimension = targetSizeMB < 5 ? 1200 : 2000;
            newWidth = Math.Max(minDimension, newWidth);
            newHeight = Math.Max(minDimension, newHeight);
            return (newWidth, newHeight);
        }

        private static int CalculateOptimalColors(int width, int height, int targetSizeMB)
        {
            long totalPixels = (long)width * height;
            if (totalPixels > 8000000) return 128;
            if (totalPixels > 4000000) return 256;
            return 512;
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
                Quantizer = new SixLabors.ImageSharp.Processing.Processors.Quantization.WuQuantizer(new QuantizerOptions { MaxColors = colors, Dither = colors <= 128 ? KnownDitherings.FloydSteinberg : null })
            };

            return encoder;
        }
        private void UiSetStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = text);
        private void UiSetProgress(double value) => Dispatcher.Invoke(() => ProgressBar.Value = value);
        private void UiEnableStart(bool enabled) => Dispatcher.Invoke(() => StartCompress_Button.IsEnabled = enabled);
        private void UiEnableStop(bool enabled) => Dispatcher.Invoke(() => StopCompress_Button.IsEnabled = enabled);
        private void UiShowMessage(string text) => Dispatcher.Invoke(() => MessageBox.Show(text));
        private void UiUpdateProgress(double value, string statusText)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = value;
                StatusText.Text = statusText;
            });
        }

        private void AboutButton_Click(object? _sender, RoutedEventArgs _e)
        {
            var dlg = new AboutWindow { Owner = this };
            dlg.ShowDialog();
        }
    }
}