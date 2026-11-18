using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>通过独立的无头 Chromium 进程捕捉 PNG 截图。</summary>
/// <remarks>每个实例管理自己的浏览器进程、临时配置目录和 DevTools 连接。</remarks>
public sealed class HtmlCapture : IAsyncDisposable
{
    /// <summary>浏览器实例专用的临时配置目录。</summary>
    private readonly DirectoryInfo profile = Directory.CreateTempSubdirectory("WinUsbDisplay-Edge-");
    /// <summary>连接单个页面的 DevTools WebSocket。</summary>
    private readonly ClientWebSocket socket = new();
    /// <summary>串行化 WebSocket 请求写入的锁。</summary>
    private readonly SemaphoreSlim sendGate = new(1);
    /// <summary>按 DevTools 请求编号跟踪等待中的应答。</summary>
    private readonly ConcurrentDictionary<int, Action<JsonElement>> pendingReplies = new();
    /// <summary>保存供读取端消费的浏览器推送帧。</summary>
    private readonly Channel<byte[]> frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
    });
    /// <summary>接收 WebSocket 消息分片时复用的缓冲区。</summary>
    private readonly byte[] receiveBuffer = new byte[64 * 1024];
    /// <summary>用于组装完整 WebSocket 消息的暂存流。</summary>
    private readonly MemoryStream receiveMessage = new();
    private readonly int maximumMessageBytes;
    /// <summary>由当前捕捉实例管理的浏览器进程。</summary>
    private Process? browser;
    /// <summary>持续排空浏览器错误管道，限制长期捕捉的诊断内存。</summary>
    private ProcessDiagnostics? browserDiagnostics;
    /// <summary>为下一条 DevTools 请求分配编号的计数器。</summary>
    private int nextId;
    /// <summary>记录当前连接是否已启动推帧。</summary>
    private bool screencastStarted;
    /// <summary>控制接收循环是否接纳浏览器推送的帧。</summary>
    private bool acceptingScreencastFrames;
    /// <summary>已启动推帧所使用的图像格式。</summary>
    private string? screencastFormat;
    /// <summary>控制整个 DevTools 连接的生命周期。</summary>
    private readonly CancellationTokenSource connectionLifetime = new();
    /// <summary>持续接收 DevTools 应答和事件的后台任务。</summary>
    private Task? receiver;
    /// <summary>控制本轮推帧等待的生命周期。</summary>
    private CancellationTokenSource? screencastLifetime;
    /// <summary>尚未完成的推帧读取任务。</summary>
    private Task<byte[]>? pendingFrame;
    /// <summary>超时复用时返回的最近一帧。</summary>
    private byte[]? lastFrame;
    /// <summary>记录实例是否已释放。</summary>
    private bool disposed;

    /// <summary>创建捕捉实例并关闭本机 DevTools 连接的代理。</summary>
    internal HtmlCapture(int maximumMessageBytes = 64 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
        this.maximumMessageBytes = maximumMessageBytes;
        socket.Options.Proxy = null;
    }

    /// <summary>打开本地 HTML 文件或 HTTP(S) 页面，按型号的像素尺寸捕捉。</summary>
    public static Task<HtmlCapture> OpenAsync(string source, DisplayModel model, CancellationToken cancellation) =>
        OpenAsync(source, model, cancellation, fastCapture: false);

    /// <summary>启动浏览器并等待页面就绪。</summary>
    /// <param name="source">本地 HTML 文件路径或绝对 HTTP(S) URL。</param>
    /// <param name="model">定义捕捉视口尺寸的设备型号。</param>
    /// <param name="cancellation">用于取消浏览器启动或页面加载。</param>
    /// <param name="fastCapture">为 true 时请求 Chromium 关闭帧率限制。</param>
    public static Task<HtmlCapture> OpenAsync(string source, DisplayModel model, CancellationToken cancellation,
        bool fastCapture) => OpenAsync(source, model, cancellation, fastCapture, pixelScale: 1);

    /// <summary>以指定像素密度捕捉页面，同时保留设备尺寸对应的 CSS 视口。</summary>
    public static async Task<HtmlCapture> OpenAsync(string source, DisplayModel model, CancellationToken cancellation,
        bool fastCapture, double pixelScale)
    {
        if (!double.IsFinite(pixelScale) || pixelScale is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(pixelScale));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Enter a local HTML file or an HTTP(S) URL.", nameof(source));
        Uri uri;
        if (Uri.TryCreate(source, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            uri = url;
        else if (await Task.Run(() => File.Exists(source), cancellation).WaitAsync(cancellation).ConfigureAwait(false))
            uri = new Uri(Path.GetFullPath(source));
        else
            throw new ArgumentException("Enter a local HTML file or an HTTP(S) URL.", nameof(source));

        cancellation.ThrowIfCancellationRequested();
        var capture = new HtmlCapture();
        try
        {
            await capture.ConnectAsync(uri, model, cancellation, fastCapture, pixelScale).ConfigureAwait(false);
            return capture;
        }
        catch
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>查找页面的 DevTools 端点并建立连接。</summary>
    internal async Task ConnectAsync(Uri source, DisplayModel model, CancellationToken cancellation, bool fastCapture,
        double pixelScale, ProcessStartInfo? browserStart = null)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        startup.CancelAfter(TimeSpan.FromSeconds(20));
        var token = startup.Token;
        string chrome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe");
        string executable = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_BROWSER")
            ?? (File.Exists(chrome) ? chrome : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge",
                "Application", "msedge.exe"));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        start.Environment.Remove("WINUSB_DISPLAY_COMMAND_KEY");
        foreach (string arg in new[] { "--headless=new", "--disable-extensions", "--no-first-run", "--no-default-browser-check", "--enable-logging=stderr",
            "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0",
            $"--user-data-dir={profile.FullName}" })
            start.ArgumentList.Add(arg);
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_BROWSER_GPU") != "1")
            start.ArgumentList.Add("--disable-gpu");
        if (fastCapture)
            start.ArgumentList.Add("--disable-frame-rate-limit");
        if (Environment.GetEnvironmentVariable("WINUSB_DISPLAY_BROWSER_RASTER_THREADS") is { } rasterThreads
            && int.TryParse(rasterThreads, out int count) && count is >= 1 and <= 8)
            start.ArgumentList.Add($"--num-raster-threads={count}");
        start.ArgumentList.Add("about:blank");
        browser = Process.Start(browserStart ?? start) ?? throw new IOException("Could not start the headless browser.");

        var diagnostics = browserDiagnostics = new ProcessDiagnostics(browser.StandardError);
        string stage = "waiting for the browser to publish its debugging port";
        try
        {
            string portFile = Path.Combine(profile.FullName, "DevToolsActivePort");
            int port = 0;
            while (port == 0)
            {
                token.ThrowIfCancellationRequested();
                if (browser.HasExited)
                {
                    await VideoConverter.FinishDiagnosticsAsync(browser.StandardError, diagnostics.Completion).ConfigureAwait(false);
                    browserDiagnostics = null;
                    string errors = diagnostics.Text;
                    throw new IOException($"Headless browser exited with code {browser.ExitCode}. {errors.Trim()}");
                }
                if (File.Exists(portFile))
                {
                    try
                    {
                        using var file = new FileStream(portFile, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var reader = new StreamReader(file);
                        int.TryParse(await reader.ReadLineAsync(token).ConfigureAwait(false), out port);
                    }
                    catch (IOException)
                    {
                        // Chromium 可能仍在创建或写入端口文件。
                    }
                }
                if (port == 0)
                    await Task.Delay(100, token).ConfigureAwait(false);
            }

            stage = "connecting to the browser debugging endpoint";
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
            using var targets = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", token).ConfigureAwait(false));
            var page = targets.RootElement.EnumerateArray().First(item => item.GetProperty("type").GetString() == "page");
            var endpoint = new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!);
            if (endpoint.Scheme != "ws" || !IPAddress.TryParse(endpoint.Host, out var address) || !IPAddress.IsLoopback(address)
                || endpoint.Port != port)
                throw new IOException("Unexpected browser debugging endpoint.");
            await ConnectSocketAsync(endpoint, token).ConfigureAwait(false);
            stage = "configuring the capture viewport";
            await SendAsync("Emulation.setDeviceMetricsOverride", new
            {
                width = model.Width, height = model.Height, deviceScaleFactor = pixelScale, mobile = false
            }, token).ConfigureAwait(false);
            await SendAsync("Emulation.setDefaultBackgroundColorOverride", new
            {
                color = new { r = 0, g = 0, b = 0, a = 0 }
            }, token).ConfigureAwait(false);
            stage = "loading the HTML page";
            var nav = await SendAsync("Page.navigate", new { url = source.AbsoluteUri }, token).ConfigureAwait(false);
            if (nav.TryGetProperty("errorText", out var error))
                throw new IOException(error.GetString());
            while (true)
            {
                var state = await SendAsync("Runtime.evaluate", new
                {
                    expression = "location.href !== 'about:blank' && document.readyState !== 'loading'", returnByValue = true
                }, token).ConfigureAwait(false);
                if (state.GetProperty("result").TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True)
                    break;
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"HTML capture startup timed out after 20 seconds while {stage}.");
        }
    }

    /// <summary>连接页面 WebSocket 并启动后台接收循环。</summary>
    internal async Task ConnectSocketAsync(Uri endpoint, CancellationToken cancellation)
    {
        await socket.ConnectAsync(endpoint, cancellation).ConfigureAwait(false);
        receiver = ReceiveResponsesAsync(connectionLifetime.Token);
    }

    /// <summary>请求当前页面的 PNG 截图。</summary>
    public ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellation) =>
        ReadFrameAsync(cancellation, jpeg: false);

    /// <summary>直接获取 JPEG 截图，供不需要透明通道的实时输出使用。</summary>
    public ValueTask<byte[]?> ReadJpegFrameAsync(CancellationToken cancellation) =>
        ReadFrameAsync(cancellation, jpeg: true);

    /// <summary>指定输出格式并向页面请求截图。</summary>
    private async ValueTask<byte[]?> ReadFrameAsync(CancellationToken cancellation, bool jpeg)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            object options = jpeg
                ? new { format = "jpeg", quality = 85, fromSurface = true }
                : new { format = "png", fromSurface = true, optimizeForSpeed = true };
            return await SendAsync("Page.captureScreenshot", options,
                timeout.Token, static result => result.GetProperty("data").GetBytesFromBase64()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The browser did not return a frame within 10 seconds.");
        }
    }

    /// <summary>读取浏览器连续推送的 JPEG 帧。调用方须串行读取。</summary>
    /// <param name="cancellation">取消本次读取。</param>
    /// <param name="quality">首次启动推帧时使用的 JPEG 质量。</param>
    /// <param name="repeatAfter">收到首帧后，等待此时长仍无新帧时复用最近一帧。</param>
    public async ValueTask<byte[]?> ReadJpegScreencastAsync(CancellationToken cancellation, int quality = 85,
        TimeSpan? repeatAfter = null) => await ReadScreencastAsync("jpeg", quality, cancellation, repeatAfter).ConfigureAwait(false);

    /// <summary>读取浏览器推送的 PNG 帧，供需要透明通道的合成路径使用。</summary>
    /// <param name="cancellation">取消等待推帧的令牌。</param>
    /// <param name="repeatAfter">收到首帧后，等待此时长仍无新帧时复用最近一帧。</param>
    public ValueTask<byte[]?> ReadPngScreencastAsync(CancellationToken cancellation, TimeSpan? repeatAfter = null)
        => ReadScreencastAsync("png", 85, cancellation, repeatAfter);

    /// <summary>按指定格式等待推送帧，并在允许时复用最近一帧。</summary>
    private async ValueTask<byte[]?> ReadScreencastAsync(string format, int quality, CancellationToken cancellation,
        TimeSpan? repeatAfter)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        if (repeatAfter is { } repeat && repeat != Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThan(repeat, TimeSpan.Zero, nameof(repeatAfter));
        cancellation.ThrowIfCancellationRequested();
        if (screencastFormat is not null && screencastFormat != format)
            throw new InvalidOperationException("The screencast format cannot change within a capture session.");
        screencastFormat = format;
        screencastLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime.Token);
        Task<byte[]> receive = pendingFrame ??= ReceiveScreencastAsync(format, quality, screencastLifetime.Token);
        bool canRepeat = lastFrame is not null && repeatAfter.HasValue;
        TimeSpan timeout = canRepeat ? repeatAfter!.Value : TimeSpan.FromSeconds(10);
        try
        {
            lastFrame = await receive.WaitAsync(timeout, cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException) when (canRepeat)
        {
            // 等待超时只影响本次调用，后续仍可读取同一个推帧任务。
            cancellation.ThrowIfCancellationRequested();
            if (!receive.IsCompleted)
                return lastFrame;
            lastFrame = await receive.ConfigureAwait(false);
        }
        finally
        {
            // 调用方超时不会废弃正在进行的读取，但失败的推帧任务不能留给后续调用。
            if (receive.IsFaulted || receive.IsCanceled)
            {
                _ = receive.Exception;
                pendingFrame = null;
            }
        }
        pendingFrame = null;
        return lastFrame;
    }

    // 此切换必须与推帧读取串行执行，截图命令可以独立运行。
    /// <summary>停止当前推帧并清理本轮读取状态。</summary>
    internal async Task StopScreencastAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellation.ThrowIfCancellationRequested();
        if (screencastLifetime is not { } lifetime)
            return;

        Volatile.Write(ref acceptingScreencastFrames, false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (pendingFrame is { } pending)
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await SendAsync("Page.stopScreencast", new { }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The browser did not stop pushing frames.");
        }
        finally
        {
            frames.Reader.TryRead(out _);
            pendingFrame = null;
            lastFrame = null;
            screencastStarted = false;
            screencastFormat = null;
            screencastLifetime = null;
            lifetime.Dispose();
        }
    }

    /// <summary>等待浏览器推帧，并在空闲时检查渲染器。</summary>
    private async Task<byte[]> ReceiveScreencastAsync(string format, int quality, CancellationToken cancellation)
    {
        if (!screencastStarted)
        {
            Volatile.Write(ref acceptingScreencastFrames, true);
            try
            {
                await SendAsync("Page.startScreencast", new { format, quality, everyNthFrame = 1 }, cancellation)
                    .ConfigureAwait(false);
            }
            catch
            {
                Volatile.Write(ref acceptingScreencastFrames, false);
                frames.Reader.TryRead(out _);
                throw;
            }
            screencastStarted = true;
        }

        using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<byte[]> frame = frames.Reader.ReadAsync(readLifetime.Token).AsTask();
        try
        {
            while (true)
            {
                try
                {
                    return await frame.WaitAsync(TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    if (frame.IsCompleted)
                        return await frame.ConfigureAwait(false);
                    // 静态页面可能停止推帧。此时探测渲染器状态，同时允许截图继续运行。
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    Task<JsonElement> probe = SendAsync("Runtime.evaluate",
                        new { expression = "1", returnByValue = true }, timeout.Token);
                    if (await Task.WhenAny(frame, probe).ConfigureAwait(false) == frame)
                    {
                        timeout.Cancel();
                        await ((Task)probe).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        return await frame.ConfigureAwait(false);
                    }

                    try
                    {
                        await probe.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                    {
                        throw new TimeoutException("The browser renderer did not answer the capture health check.");
                    }
                }
            }
        }
        catch (ChannelClosedException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error.InnerException);
            throw;
        }
        finally
        {
            readLifetime.Cancel();
            await ((Task)frame).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>将收到的 DevTools 消息分派给请求调用方或推帧队列。</summary>
    private async Task ReceiveResponsesAsync(CancellationToken cancellation)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                using var doc = await ReceiveMessageAsync(cancellation).ConfigureAwait(false);
                var message = doc.RootElement;
                // 命令应答按请求 ID 分发，浏览器主动推送的帧走独立队列。
                if (message.TryGetProperty("id", out var responseId))
                {
                    if (pendingReplies.TryRemove(responseId.GetInt32(), out var complete))
                        complete(message);
                }
                else if (message.TryGetProperty("method", out var method) && method.ValueEquals("Page.screencastFrame"))
                {
                    var frame = message.GetProperty("params");
                    // 即使正在切换捕捉模式并丢弃画面，也要确认推送帧，让 Chromium 继续推进。
                    await SendRequestAsync(Interlocked.Increment(ref nextId), "Page.screencastFrameAck",
                        new { sessionId = frame.GetProperty("sessionId").GetInt32() }, cancellation).ConfigureAwait(false);
                    if (Volatile.Read(ref acceptingScreencastFrames))
                        frames.Writer.TryWrite(frame.GetProperty("data").GetBytesFromBase64());
                }
            }
        }
        catch (WebSocketException error)
        {
            failure = new IOException("Browser capture connection failed.", error);
            throw failure;
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            frames.Writer.TryComplete(failure);
        }
    }

    /// <summary>串行写入带请求编号的 DevTools 命令。</summary>
    private async Task SendRequestAsync(int id, string method, object parameters, CancellationToken cancellation)
    {
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await sendGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // 调用方等待超时后可以丢弃对应应答。连接只在 WebSocket 写入超时时关闭。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!connectionLifetime.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("The browser capture connection did not accept a request.");
        }
        catch (WebSocketException error)
        {
            throw new IOException("Browser capture connection failed.", error);
        }
        finally
        {
            sendGate.Release();
        }
    }

    /// <summary>拼接 WebSocket 消息分片并解析 JSON。</summary>
    private async Task<JsonDocument> ReceiveMessageAsync(CancellationToken cancellation)
    {
        // 接收端在复用缓冲区前会释放当前 JSON 文档。
        receiveMessage.SetLength(0);
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(receiveBuffer.AsMemory(), cancellation).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close)
                throw new IOException("Browser capture connection closed.");
            if (part.MessageType != WebSocketMessageType.Text)
                throw new IOException("The browser returned a non-text DevTools message.");
            if (part.Count > maximumMessageBytes - receiveMessage.Length)
                throw new IOException($"The browser DevTools message exceeds {maximumMessageBytes} bytes.");
            receiveMessage.Write(receiveBuffer, 0, part.Count);
        } while (!part.EndOfMessage);

        try
        {
            return JsonDocument.Parse(receiveMessage.GetBuffer().AsMemory(0, (int)receiveMessage.Length));
        }
        catch (JsonException error)
        {
            throw new IOException("The browser returned invalid DevTools JSON.", error);
        }
    }

    /// <summary>发送 DevTools 请求并等待应答。</summary>
    private Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellation) =>
        SendAsync(method, parameters, cancellation, static result => result.Clone());


    /// <summary>向 DevTools 发送带参数的命令。</summary>
    private async Task<T> SendAsync<T>(string method, object parameters, CancellationToken cancellation,
        Func<JsonElement, T> readResult)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Task receive = receiver ?? throw new InvalidOperationException("The browser is not connected.");
        int id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingReplies[id] = response =>
        {
            try
            {
                if (response.TryGetProperty("error", out var error))
                    throw new IOException($"{method}: {error.GetProperty("message").GetString()}");
                reply.TrySetResult(readResult(response.GetProperty("result")));
            }
            catch (Exception error)
            {
                reply.TrySetException(error);
            }
        };
        try
        {
            if (!receive.IsCompleted)
                await SendRequestAsync(id, method, parameters, cancellation).ConfigureAwait(false);
            Task finished = await Task.WhenAny(reply.Task, receive).WaitAsync(cancellation).ConfigureAwait(false);
            // 已收到的应答在浏览器紧接着断开连接时仍然有效。
            if (finished == receive && !reply.Task.IsCompleted)
                await receive.ConfigureAwait(false);
            return await reply.Task.ConfigureAwait(false);
        }
        finally
        {
            pendingReplies.TryRemove(id, out _);
            reply.TrySetCanceled();
        }
    }

    /// <summary>关闭捕捉连接和浏览器，并尝试删除临时配置目录。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        connectionLifetime.Cancel();
        socket.Dispose();
        if (receiver is not null)
            await receiver.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (pendingFrame is not null)
            await ((Task)pendingFrame).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        screencastLifetime?.Dispose();
        receiveMessage.Dispose();
        connectionLifetime.Dispose();
        if (browser is not null)
        {
            using (browser)
            {
                if (!browser.HasExited)
                {
                    try { browser.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (browser.HasExited) { }
                    await browser.WaitForExitAsync().ConfigureAwait(false);
                }
                if (browserDiagnostics is { } diagnostics)
                    await VideoConverter.FinishDiagnosticsAsync(browser.StandardError, diagnostics.Completion).ConfigureAwait(false);
            }
        }
        // 主进程退出后，Chromium 子进程仍可能短暂持有配置目录里的文件。
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (browser is not null)
                await Task.Delay(100).ConfigureAwait(false);
            try
            {
                profile.Delete(recursive: true);
                return;
            }
            catch (IOException error) when (browser is not null && attempt < 4
                && error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) { }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
