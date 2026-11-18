using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>找不到配置的 FFmpeg 可执行文件时引发。</summary>
public sealed class FfmpegNotFoundException : IOException
{
    /// <summary>保存尝试启动的 FFmpeg 路径及底层异常。</summary>
    internal FfmpegNotFoundException(string executable, Exception innerException)
        : base($"FFmpeg executable not found: {executable}", innerException) { }
}

/// <summary>表示 FFmpeg 已启动但处理失败。</summary>
/// <param name="exitCode">FFmpeg 退出码。</param>
/// <param name="diagnostics">FFmpeg 的诊断输出。</param>
/// <param name="innerException">可选的底层异常。</param>
internal sealed class FfmpegProcessException(int exitCode, string diagnostics, Exception? innerException = null)
    : IOException($"FFmpeg exited with code {exitCode}. {diagnostics}".TrimEnd(), innerException)
{
    /// <summary>FFmpeg 进程的退出码。</summary>
    internal int ExitCode { get; } = exitCode;
    /// <summary>FFmpeg 进程输出的诊断文案。</summary>
    internal string Diagnostics { get; } = diagnostics;
}

/// <summary>使用 FFmpeg 转换设备视频、封装预览并读取源帧率。</summary>
public static class VideoConverter
{
    private static readonly TimeSpan DiagnosticsGracePeriod = TimeSpan.FromSeconds(1);
    // 大写 V 排除封面和缩略图，转换与帧率探测都选择同一条视频流。
    /// <summary>选取首个普通视频流的 FFmpeg 映射表达式。</summary>
    internal const string PrimaryVideoStream = "0:V:0";

    internal const string PartialOutputMarker = ".partial";

    /// <summary>持久化 FFmpeg 可执行文件路径的配置文件。</summary>
    private static string ExecutableConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TURZX.WinUsbDisplay", "ffmpeg-path.txt");

    /// <summary>验证并保存供后续媒体操作使用的 FFmpeg 路径。</summary>
    public static void ConfigureExecutable(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("FFmpeg executable was not found.", fullPath);

        string config = ExecutableConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        string pending = config + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(pending, fullPath);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(pending, config, overwrite: true);
                    break;
                }
                catch (Exception error) when (attempt < 5 && OperatingSystem.IsWindows()
                    && error.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020))
                {
                    // 其他进程读取文件时，Windows 可能暂时拒绝替换。
                    Thread.Sleep(15);
                }
            }
        }
        finally
        {
            File.Delete(pending);
        }
    }

    /// <summary>从配置和环境解析用于媒体处理的 FFmpeg 可执行文件。</summary>
    internal static string ResolveExecutable() =>
        ResolveExecutable(ExecutableConfigPath, Environment.GetEnvironmentVariable("WINUSB_DISPLAY_FFMPEG"));

    internal static string ResolveExecutable(string configPath, string? environment)
    {
        try
        {
            using var file = new FileStream(configPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(file);
            string configured = reader.ReadToEnd().Trim();
            if (Path.IsPathFullyQualified(configured) && File.Exists(configured))
                return configured;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 无法读取偏好文件时，仍可使用环境变量、随包程序或 PATH。
        }

        if (environment is { Length: > 0 })
            return environment;

        string adjacent = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        return File.Exists(adjacent) ? adjacent : "ffmpeg";
    }

    /// <summary>启动 FFmpeg 并将找不到程序的错误转换为专用异常。</summary>
    internal static Process StartFfmpeg(ProcessStartInfo start)
    {
        start.Environment.Remove("WINUSB_DISPLAY_COMMAND_KEY");
        try
        {
            return Process.Start(start) ?? throw new IOException("Could not start FFmpeg.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            throw new FfmpegNotFoundException(start.FileName, ex);
        }
    }

    /// <summary>按设备尺寸、帧率和可选布局转码为 Annex B H.264，使用默认方向。</summary>
    public static Task ConvertAsync(string source, string destination, DisplayModel model, byte fps,
        CancellationToken cancellation = default, MediaLayout? layout = null) =>
        ConvertAsync(source, destination, model, fps, cancellation, layout, 0);

    /// <summary>按指定布局转码为 Annex B H.264，可将画面旋转到设备原生方向。</summary>
    public static Task ConvertAsync(string source, string destination, DisplayModel model, byte fps,
        CancellationToken cancellation, MediaLayout? layout, byte rotation) =>
        ConvertAsync(source, destination, model, fps, cancellation, layout, rotation, executable: null);

    /// <summary>通过 FFmpeg 将源视频转成设备所需的 H.264，并报告转码进度。</summary>
    internal static async Task ConvertAsync(string source, string destination, DisplayModel model, byte fps,
        CancellationToken cancellation, MediaLayout? layout, byte rotation, string? executable)
    {
        ArgumentOutOfRangeException.ThrowIfZero(fps);
        if (model.Width <= 0 || model.Height <= 0 || model.Width % 2 != 0 || model.Height % 2 != 0)
            throw new ArgumentException("Video dimensions must be positive and even.", nameof(model));

        MediaLayout place = layout ?? MediaLayout.Default;
        bool compactEncoding = model.ProductId == 0x0046;
        string filter = BuildLayoutFilter(model.ForRotation(rotation), place) + NativeRotationFilter(rotation);

        string frameRate = fps.ToString(CultureInfo.InvariantCulture);
        string sourcePath = Path.GetFullPath(source);
        await WriteOutputAsync(destination, (pending, token) => RunAsync(["-i",
            sourcePath, "-map", PrimaryVideoStream, "-vf", filter, "-r", frameRate,
            "-an", "-c:v", "libx264", "-preset", compactEncoding ? "veryfast" : "fast",
            "-crf", compactEncoding ? "28" : "20", "-bf", "0", "-pix_fmt", "yuv420p",
            "-f", "h264", pending], token, executable), cancellation).ConfigureAwait(false);
    }

    /// <summary>将设备旋转方向转换为 FFmpeg 画面旋转滤镜。</summary>
    internal static string NativeRotationFilter(byte rotation) => rotation switch
    {
        0 => "",
        1 => ",transpose=cclock",
        2 => ",hflip,vflip",
        3 => ",transpose=clock",
        _ => throw new ArgumentOutOfRangeException(nameof(rotation))
    };

    /// <summary>生成视频适配、缩放、偏移和裁剪所需的 FFmpeg 滤镜。</summary>
    internal static string BuildLayoutFilter(DisplayModel model, MediaLayout layout)
    {
        int x = Math.Clamp(layout.OffsetX, -model.Width, model.Width);
        int y = Math.Clamp(layout.OffsetY, -model.Height, model.Height);
        string round = layout.Fill ? "ceil" : "floor";
        string bound = layout.Fill ? "max" : "min";
        // 显示宽高比（DAR）包含输入的像素宽高比。先算出方形像素画面的尺寸，再填充边缘。
        string scale = FormattableString.Invariant(
            $"scale=w='max(2,2*{round}({bound}({model.Width},{model.Height}*dar)/2))':h='max(2,2*{round}({bound}({model.Height},{model.Width}/dar)/2))',setsar=1,");
        double zoom = layout.ZoomFactor;
        string zoomFilter = zoom == 1 ? "" : FormattableString.Invariant(
            $"scale=max(2\\,trunc(iw*{zoom}/2)*2):max(2\\,trunc(ih*{zoom}/2)*2),");
        // 即使源画面已铺满画布，也要为最终裁剪前的平移预留空间。
        return scale + zoomFilter + "format=yuv444p," +
            FormattableString.Invariant(
                $"pad=max(iw\\,{model.Width})+{2L * Math.Abs(x)}:max(ih\\,{model.Height})+{2L * Math.Abs(y)}:(ow-iw)/2:(oh-ih)/2,") +
            FormattableString.Invariant(
                $"crop={model.Width}:{model.Height}:(iw-ow)/2-({x}):(ih-oh)/2-({y}),setsar=1");
    }

    /// <summary>将 Annex B H.264 封装为可预览的视频文件，保留原视频编码。</summary>
    public static Task CreatePreviewAsync(string annexB, string destination, byte fps,
        CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(fps);
        string sourcePath = Path.GetFullPath(annexB);
        string frameRate = fps.ToString(CultureInfo.InvariantCulture);
        return WriteOutputAsync(destination, (pending, token) => RunAsync(
            ["-r", frameRate, "-f", "h264", "-i", sourcePath, "-map", "0:v:0", "-an",
             "-c:v", "copy", "-movflags", "+faststart", pending], token), cancellation);
    }

    internal static async Task WriteOutputAsync(string destination, Func<string, CancellationToken, Task> write,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"Output path already exists: {target}");

        // 保留扩展名供 FFmpeg 推断容器格式，并确保发布时在同一目录内移动。
        string pending = Path.Combine(Path.GetDirectoryName(target)!,
            $"{Guid.NewGuid():N}{PartialOutputMarker}{Path.GetExtension(target)}");
        try
        {
            await write(pending, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(pending, target);
        }
        catch
        {
            try { File.Delete(pending); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 保留转码、取消或发布时的原始异常。
            }
            throw;
        }
    }

    /// <summary>获取设备播放使用的整数帧率，限制在 1 至 60。无法识别时使用 20 FPS。</summary>
    public static async Task<byte> DetectPlaybackFrameRateAsync(string source, CancellationToken cancellation)
    {
        double fps = await DetectSourceFrameRateAsync(source, cancellation).ConfigureAwait(false) ?? 20;
        return (byte)Math.Clamp(Math.Floor(fps), 1, 60);
    }

    /// <summary>读取源文件主视频流报告的帧率，无法识别时返回空。</summary>
    public static async Task<double?> DetectSourceFrameRateAsync(string source, CancellationToken cancellation)
    {
        try
        {
            string description = await RunAsync(["-loglevel", "info", "-i", Path.GetFullPath(source),
                "-map", PrimaryVideoStream, "-c:v", "copy", "-t", "0", "-f", "null", "-"],
                cancellation).ConfigureAwait(false);
            return ParseSelectedFrameRate(description);
        }
        catch (FfmpegProcessException error) when (error.Diagnostics.Contains(
            $"Stream map '{PrimaryVideoStream}' matches no streams.", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>从 FFmpeg 日志提取实际选中视频流的帧率。</summary>
    internal static double? ParseSelectedFrameRate(string description)
    {
        var mapping = System.Text.RegularExpressions.Regex.Match(description, @"(?m)^[ \t]*Stream #0:(\d+) -> #0:0\b");
        if (!mapping.Success)
            return null;

        // 编号相同的输出流可能标注不同的帧率。
        int output = description.IndexOf("\nOutput #", StringComparison.Ordinal);
        string input = description[..(output < 0 ? mapping.Index : Math.Min(output, mapping.Index))];
        var match = System.Text.RegularExpressions.Regex.Match(input,
            $@"(?m)^[ \t]*Stream #0:{mapping.Groups[1].Value}(?!\d)[^\r\n]*?: Video:[^\r\n]*?\b(\d+(?:\.\d+)?) fps\b");
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out double fps) && double.IsFinite(fps) && fps > 0)
            return fps;

        return null;
    }

    /// <summary>运行 FFmpeg，收集诊断输出并检查退出状态。</summary>
    private static async Task<string> RunAsync(string[] arguments, CancellationToken cancellation, string? executable = null)
    {
        var start = new ProcessStartInfo(executable ?? ResolveExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-n" }.Concat(arguments))
            start.ArgumentList.Add(argument);

        return await RunAsync(start, cancellation).ConfigureAwait(false);
    }

    internal static async Task<string> RunAsync(ProcessStartInfo start, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = StartFfmpeg(start);
        using var stderrReader = process.StandardError;
        var diagnostics = new ProcessDiagnostics(stderrReader);
        Task stderr = diagnostics.Completion;
        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            try
            {
                // 已退出的主进程可能留下仍持有管道的子进程，不能无限等待 EOF。
                await stderr.WaitAsync(DiagnosticsGracePeriod, cancellation).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            cancellation.ThrowIfCancellationRequested();
            string details = diagnostics.Text;
            if (process.ExitCode != 0)
                throw new FfmpegProcessException(process.ExitCode, details.Trim());
            return details;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            // 关闭本地读端，并观察关闭后可能迟到的管道异常。
            stderrReader.Dispose();
            _ = ObserveDiagnosticsAsync(stderr);
        }

    }

    /// <summary>有界等待诊断管道，并关闭可能被遗留子进程保持打开的本地读端。</summary>
    internal static async Task FinishDiagnosticsAsync(StreamReader reader, Task diagnostics)
    {
        try
        {
            await diagnostics.WaitAsync(DiagnosticsGracePeriod)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        finally
        {
            reader.Dispose();
            _ = ObserveDiagnosticsAsync(diagnostics);
        }
    }

    private static async Task ObserveDiagnosticsAsync(Task diagnostics) =>
        await diagnostics.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
