using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using TURZX.WinUsbDisplay;
using TURZX.WinUsbDisplay.Media;

// 显式运行的离线集成验收，需要本机 Chromium；不连接设备或访问外部页面。
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var token = deadline.Token;
var directory = Directory.CreateTempSubdirectory("WinUsbDisplay-Smoke-");
try
{
    string page = Path.Combine(directory.FullName, "test.html");
    await File.WriteAllTextAsync(page, """
        <!doctype html><html><head><meta charset="utf-8"></head>
        <body style="margin:0;background:rgb(255,0,0)"><p>Offline capture</p></body></html>
        """, token);
    var model = new DisplayModel("Offline smoke", 0, 64, 96);
    long started = Stopwatch.GetTimestamp();
    var capture = await HtmlCapture.OpenAsync(page, model, token, fastCapture: false, pixelScale: .5);
    double startupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    double disposeMs;
    bool mediaVerified = false;
    try
    {
        byte[] png = await capture.ReadFrameAsync(token) ?? throw new IOException("No PNG screenshot.");
        Require(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
        Require(BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)) == 32 &&
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)) == 48, "Scaled viewport dimensions");
        byte[] jpeg = await capture.ReadJpegFrameAsync(token) ?? throw new IOException("No JPEG screenshot.");
        Require(jpeg.AsSpan().StartsWith(new byte[] { 255, 216, 255 }), "JPEG signature");
        byte[] pushed = await capture.ReadPngScreencastAsync(token) ?? throw new IOException("No pushed frame.");
        Require(pushed.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }), "Screencast PNG signature");
        Require(await capture.ReadPngScreencastAsync(token, TimeSpan.Zero) is { Length: > 0 }, "Static frame reuse");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await capture.ReadFrameAsync(cancelled.Token);
            throw new IOException("A cancelled screenshot succeeded.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        Require(await capture.ReadFrameAsync(token) is { Length: > 0 }, "Capture after cancellation");
        if (args.Contains("--media"))
        {
            string image = Path.Combine(directory.FullName, "frame.png");
            string video = Path.Combine(directory.FullName, "frame.h264");
            string preview = Path.Combine(directory.FullName, "preview.mp4");
            await File.WriteAllBytesAsync(image, png, token);
            await VideoConverter.ConvertAsync(image, video, model, 20, token);
            Require(new FileInfo(video).Length > 4, "FFmpeg H.264 output");
            byte[] prefix = new byte[4];
            using (var stream = File.OpenRead(video))
                await stream.ReadExactlyAsync(prefix, token);
            Require(prefix.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 1 }) ||
                prefix.AsSpan(0, 3).SequenceEqual(new byte[] { 0, 0, 1 }), "Annex B prefix");
            await VideoConverter.CreatePreviewAsync(video, preview, 20, token);
            Require(new FileInfo(preview).Length > 8, "FFmpeg MP4 preview");
            Require(await VideoConverter.DetectSourceFrameRateAsync(preview, token) is > 0, "FFmpeg frame-rate detection");
            await using (var decoder = new LoopingVideoFrames(preview, model, 20))
            {
                var pixels = new byte[model.Width * model.Height * 4];
                await decoder.ReadAsync(pixels, token);
                Require(pixels.Any(value => value != 0), "FFmpeg decoded pixels");
                // 单帧预览循环后仍可读取，释放时必须停止后台解码进程。
                await decoder.ReadAsync(pixels, token);
            }
            mediaVerified = true;
        }
    }
    finally
    {
        started = Stopwatch.GetTimestamp();
        await capture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), token);
        disposeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result = "passed", scope = "real Chromium, local HTML, PNG/JPEG, viewport, screencast, cancellation, disposal",
        startupMs, disposeMs, mediaVerified
    }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    directory.Delete(recursive: true);
}

static void Require(bool condition, string check)
{
    if (!condition)
        throw new InvalidDataException($"Failed: {check}");
}
