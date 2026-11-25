using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TURZX.WinUsbDisplay.Media;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 管理设备送屏性能测试和最近一次的测量结果。
public partial class MainWindow
{
    /// <summary>记录送屏帧数、测量时长和各处理阶段的耗时。</summary>
    /// <param name="Frames">完成送屏的帧数。</param>
    /// <param name="Seconds">测量时长，单位为秒。</param>
    /// <param name="CaptureMs">每帧平均捕捉耗时，单位为毫秒。静态测试为 NaN。</param>
    /// <param name="EncodeMs">每帧平均编码耗时，单位为毫秒。</param>
    /// <param name="UsbMs">每帧平均 USB 传输耗时，单位为毫秒。</param>
    private sealed record SendBenchmark(int Frames, double Seconds, double CaptureMs, double EncodeMs, double UsbMs);

    /// <summary>按设备接口路径保存最近完成的性能测试。</summary>
    private readonly Dictionary<string, SendBenchmark> sendBenchmarks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>跟踪正在执行送屏性能测试的设备。</summary>
    private readonly HashSet<string> runningBenchmarks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>更新所选设备的测试状态和结果展示。</summary>
    private void UpdateSendBenchmarkControls()
    {
        if (StartSendBenchmark is null)
            return;

        string? path = SelectedDevice?.Path;
        bool running = path is not null && runningBenchmarks.Contains(path);
        StartSendBenchmark.IsEnabled = SendBenchmarkMode.IsEnabled = !busy && !HasPendingResume && path is not null;
        StopSendBenchmark.IsEnabled = running && CurrentPlayback?.Cancellation.IsCancellationRequested == false;
        if (running)
            SendBenchmarkResult.Text = T("BenchmarkRunning");
        else if (path is not null && sendBenchmarks.TryGetValue(path, out var result))
            SendBenchmarkResult.Text = string.Join(Environment.NewLine,
                T("BenchmarkOutputRate", result.Frames / result.Seconds, result.Frames, result.Seconds),
                double.IsNaN(result.CaptureMs)
                    ? T("BenchmarkStaticImageNotice")
                    : T("BenchmarkCaptureAndEncodeTime", result.CaptureMs, result.EncodeMs),
                T("BenchmarkUsbTime", result.UsbMs));
        else
            SendBenchmarkResult.Text = T("BenchmarkOutputNotice");
    }

    /// <summary>启动所选设备的送屏测试并记录结果。</summary>
    /// <param name="Frames">完成送屏的帧数。</param>
    /// <param name="Seconds">测量时长，单位为秒。</param>
    /// <param name="CaptureMs">捕捉耗时，单位为毫秒。</param>
    /// <param name="EncodeMs">编码耗时，单位为毫秒。</param>
    /// <param name="UsbMs">USB 传输耗时，单位为毫秒。</param>
    private async void OnStartSendBenchmark(object sender, RoutedEventArgs e)
    {
        if (busy || HasPendingResume || SelectedDevice is not { } device)
            return;

        bool animated = SendBenchmarkMode.SelectedIndex == 1;
        byte rotation = (byte)PreviewQuarterTurns;
        runningBenchmarks.Add(device.Path);
        sendBenchmarks.Remove(device.Path);
        try
        {
            await RunPlaybackAsync(device, async active =>
            {
                var result = await Task.Run(() => MeasureSendAsync(device, rotation, animated, active.Token), active.Token);
                sendBenchmarks[device.Path] = result;
                SetPlaybackStatus(active, T("BenchmarkComplete"));
            }, T("BenchmarkRunning"));
        }
        finally
        {
            runningBenchmarks.Remove(device.Path);
            if (!closing)
                UpdateSendBenchmarkControls();
        }
    }

    /// <summary>测量固定画面或动态画面的捕捉、编码及 USB 传输耗时。</summary>
    private static async Task<SendBenchmark> MeasureSendAsync(DisplayDevice device, byte rotation, bool animated,
        CancellationToken token)
    {
        byte frameRotation = MediaFrameRotation(device.Model, rotation);
        DisplayModel canvas = device.Model.ForRotation(rotation);
        await using var html = animated
            ? await HtmlCapture.OpenAsync(Path.Combine(ScenesDirectory, "race.html"), canvas, token, fastCapture: true)
            : null;
        byte[] fixedFrame = html is null ? CreateBenchmarkFrame(device.Model, frameRotation) : [];

        byte[] key = CommandKeyStore.Read();
        try
        {
            using var session = OpenPlaybackDevice(device, key, rotation, token);
            double captureMs = 0, encodeMs = 0, usbMs = 0;
            int frames = 0;
            var clock = Stopwatch.StartNew();
            do
            {
                token.ThrowIfCancellationRequested();
                byte[] frame = fixedFrame;
                if (html is not null)
                {
                    long start = Stopwatch.GetTimestamp();
                    byte[] screenshot = await html.ReadFrameAsync(token)
                        ?? throw UiError(new IOException("No browser frame received."), "ErrorNoBrowserFrame");
                    captureMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    start = Stopwatch.GetTimestamp();
                    frame = DisplayFrameProcessor.PrepareLiveHtmlFrame(screenshot, MediaLayout.Default, null,
                        MediaLayout.Default, device.Model, preserveTransparency: false, rotation: frameRotation, crop: null);
                    encodeMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }

                token.ThrowIfCancellationRequested();
                long sent = Stopwatch.GetTimestamp();
                session.SendEncodedOverlay(frame);
                usbMs += Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                frames++;
            } while (clock.Elapsed < TimeSpan.FromSeconds(10));

            return new SendBenchmark(frames, clock.Elapsed.TotalSeconds,
                animated ? captureMs / frames : double.NaN, encodeMs / frames, usbMs / frames);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>根据目标型号和方向创建性能测试使用的帧。</summary>
    private static byte[] CreateBenchmarkFrame(DisplayModel model, byte rotation)
    {
        var canvas = model.ForRotation(rotation);
        var visual = new DrawingVisual();
        Brush[] colors = [Brushes.White, Brushes.Gold, Brushes.Cyan, Brushes.LimeGreen, Brushes.Magenta, Brushes.Crimson, Brushes.RoyalBlue];
        using (var drawing = visual.RenderOpen())
        {
            double width = (double)canvas.Width / colors.Length;
            for (int i = 0; i < colors.Length; i++)
                drawing.DrawRectangle(colors[i], null, new Rect(i * width, 0, width, canvas.Height * .75));
            drawing.DrawRectangle(Brushes.Black, null, new Rect(0, canvas.Height * .75, canvas.Width, canvas.Height * .25));
        }

        var bitmap = new RenderTargetBitmap(canvas.Width, canvas.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return DisplayFrameProcessor.PrepareBitmapLayers(bitmap, MediaLayout.Default, null, MediaLayout.Default,
            model, preserveTransparency: false, rotation: rotation);
    }
}
