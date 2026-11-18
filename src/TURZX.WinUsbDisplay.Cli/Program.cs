using System.Security.Cryptography;
using TURZX.WinUsbDisplay;
using TURZX.WinUsbDisplay.Media;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        TURZX WinUsbDisplay CLI
          list
          info <device>
          image <device> <file.jpg|file.png>
          video <device> <file> <fps> [--loop]
          frames <device> <fps> [<background-video> <background-fps>]
          stop <device>
          brightness <device> <0..100>
        Device indexes are zero-based, as printed by list.
        Video accepts raw Annex B H.264 or a video file readable by FFmpeg.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (args is ["list"])
    {
        var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
        for (int index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            Console.WriteLine($"{index}  {device.Model.Name}  1CBE:{device.Model.ProductId:X4}  {device.Model.Width}x{device.Model.Height}  {device.Path}");
        }
        if (devices.Count == 0)
            Console.WriteLine("No matching Win207 interfaces found.");
        return 0;
    }

    var selected = new Lazy<DisplayDevice>(() => SelectDevice(args[1]));
    var key = new Lazy<byte[]>(DisplaySettings.ReadCommandKey);
    try
    {
        switch (args)
        {
            case ["info", _]:
                using (var session = selected.Value.Open(key.Value))
                    Console.WriteLine(session.ReadFirmwareVersion(cancellation.Token));
                break;
            case ["image", _, var path]:
                var imageDevice = selected.Value;
                byte[] imageKey = key.Value;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    long length = file.Length;
                    if (length is < 1 or > DisplaySession.MaxEncodedFrameBytes)
                        throw new InvalidDataException($"Display images must be between 1 and {DisplaySession.MaxEncodedFrameBytes} bytes.");
                    byte[] image = new byte[(int)length];
                    await file.ReadExactlyAsync(image, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    using var session = imageDevice.Open(imageKey);
                    session.ShowImage(image);
                }
                break;
            case ["frames", _, var inputRate, .. var background]
                when byte.TryParse(inputRate, out byte captureFps) && captureFps is >= 1 and <= 60
                && background.Length is 0 or 2:
                await SendFramesAsync(selected, key, captureFps, background, cancellation.Token);
                break;
            case ["stop", _]:
                using (var session = selected.Value.Open(key.Value))
                    session.StopPlayback();
                break;
            case ["brightness", _, var value] when byte.TryParse(value, out byte brightness) && brightness <= 100:
                using (var session = selected.Value.Open(key.Value))
                    session.SetBrightness(brightness);
                break;
            case ["video", _, var source, var rate, .. var options]
                when byte.TryParse(rate, out byte fps) && fps is >= 1 and <= 60 && options is [] or ["--loop"]:
                var videoDevice = selected.Value;
                byte[] videoKey = key.Value;
                // 先转码再打开 USB 会话，避免在等 FFmpeg 时占用设备。
                bool convert = Path.GetExtension(source).ToLowerInvariant() is not (".h264" or ".264");
                string pathToSend = convert ? Path.Combine(Path.GetTempPath(), $"WinUsbDisplay-{Guid.NewGuid():N}.h264") : source;
                try
                {
                    if (convert)
                        await VideoConverter.ConvertAsync(source, pathToSend, videoDevice.Model, fps, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    using var stream = File.OpenRead(pathToSend);
                    using var session = videoDevice.Open(videoKey);
                    session.PlayVideo(stream, fps, options.Length == 1, cancellation.Token);
                }
                finally
                {
                    if (convert)
                        DeleteTemporaryVideo(pathToSend);
                }
                break;
            default:
                throw new ArgumentException("Invalid command or arguments. See --help.");
        }
    }
    finally
    {
        if (key.IsValueCreated)
            CryptographicOperations.ZeroMemory(key.Value);
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (PlaybackStopException ex)
{
    Console.Error.WriteLine($"{ex.Message} {ex.InnerException?.Message}");
    return 1;
}
catch (DisplayOpenException ex)
{
    Console.Error.WriteLine(ex.Failure == DisplayOpenFailure.InUse
        ? "The display is in use. Stop output in the other application and try again."
        : "Windows denied access to the display. Check the driver and device permissions.");
    return ex.Failure == DisplayOpenFailure.InUse ? 2 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static DisplayDevice SelectDevice(string index)
{
    if (!int.TryParse(index, out int deviceIndex) || deviceIndex < 0)
        throw new ArgumentException("Select a device index printed by list.");
    var devices = DisplayDevice.Discover(DisplaySettings.InterfaceGuid);
    if (deviceIndex >= devices.Count)
        throw new ArgumentException("Select a device index printed by list.");
    return devices[deviceIndex];
}

// 将标准输入中的帧持续送往目标设备。
static async Task SendFramesAsync(Lazy<DisplayDevice> selected, Lazy<byte[]> key, byte captureFps, string[] background,
    CancellationToken cancellation)
{
    byte videoFps = 24;
    if (background.Length > 0 && (!byte.TryParse(background[1], out videoFps) || videoFps is < 1 or > 60))
        throw new ArgumentException("Background FPS must be between 1 and 60.");

    DisplayDevice device = selected.Value;
    byte[] commandKey = key.Value;
    string? source = background.FirstOrDefault();
    bool convert = source is not null && Path.GetExtension(source).ToLowerInvariant() is not (".h264" or ".264");
    string? prepared = convert ? Path.Combine(Path.GetTempPath(), $"WinUsbDisplay-{Guid.NewGuid():N}.h264") : source;
    try
    {
        if (convert)
            await VideoConverter.ConvertAsync(source!, prepared!, device.Model, videoFps, cancellation);
        using var video = prepared is null ? null : File.OpenRead(prepared);
        using var input = Console.OpenStandardInput();
        using var session = device.Open(commandKey);
        await LiveScene.RunAsync(session, token => FrameInput.ReadDisplayPngAsync(input, token), video, videoFps,
            captureFps, cancellation);
    }
    finally
    {
        if (convert)
            DeleteTemporaryVideo(prepared!);
    }
}

// 尝试删除转码生成的临时视频。
static void DeleteTemporaryVideo(string path)
{
    try
    {
        File.Delete(path);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Could not remove the temporary video '{path}': {error.Message}");
    }
}
