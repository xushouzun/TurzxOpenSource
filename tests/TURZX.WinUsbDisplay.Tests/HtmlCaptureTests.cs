using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TURZX.WinUsbDisplay.Media;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证 DevTools 截图和推帧的并发消息分派。</summary>
public class HtmlCaptureTests
{
    [Fact]
    public void BrowserDiscoveryPrefersPerUserChromeAndFallsBackToInstalledEdge()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string system = Path.Combine(directory.FullName, "system");
            string user = Path.Combine(directory.FullName, "user");
            string edge = CreateExecutable(system, "Microsoft", "Edge", "msedge.exe");
            string chrome = CreateExecutable(user, "Google", "Chrome", "chrome.exe");

            Assert.Equal(chrome, HtmlCapture.ResolveBrowserExecutable(system, user));
            File.Delete(chrome);
            Assert.Equal(edge, HtmlCapture.ResolveBrowserExecutable(system, user));
            File.Delete(edge);
            Assert.Throws<FileNotFoundException>(() => HtmlCapture.ResolveBrowserExecutable(system, user));
        }
        finally
        {
            directory.Delete(recursive: true);
        }

        static string CreateExecutable(string root, string vendor, string browser, string executable)
        {
            string path = Path.Combine(root, vendor, browser, "Application", executable);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return path;
        }
    }

    [Fact]
    public async Task BrowserRotationRejectsUnsupportedAnglesBeforeStartingBrowser() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => HtmlCapture.OpenAsync("unused.html",
            new DisplayModel("test", 0, 96, 64), CancellationToken.None, true, 1, 4));

    [Fact]
    public async Task BrowserRotationRejectsRemoteFramesBeforeStartingBrowser() =>
        await Assert.ThrowsAsync<NotSupportedException>(() => HtmlCapture.OpenAsync("https://example.invalid/",
            new DisplayModel("test", 0, 96, 64), CancellationToken.None, true, 1, 1));

    /// <summary>验证空闲推帧不会阻塞截图或读走其应答。</summary>
    [Fact]
    public async Task IdleScreencastDoesNotBlockScreenshotsOrConsumeTheirReplies()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var screenshotReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task peer = RunBrowserAsync();
        try
        {
            await using var capture = new HtmlCapture();
            await capture.ConnectSocketAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"), token);
            await Assert.ThrowsAsync<IOException>(() => capture.ReadPngScreencastAsync(token).AsTask());
            Assert.Equal(new byte[] { 1, 2, 3 }, await capture.ReadPngScreencastAsync(token));
            Assert.Equal(new byte[] { 1, 2, 3 }, await capture.ReadPngScreencastAsync(token, TimeSpan.Zero));

            // 有限等待同样可以复用最近帧，并保留正在等待新帧的任务。
            Assert.Equal(new byte[] { 1, 2, 3 }, await capture.ReadPngScreencastAsync(token, TimeSpan.FromMilliseconds(20)));

            using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<byte[]?> first = capture.ReadFrameAsync(abandoned.Token).AsTask();
            await screenshotReceived.Task.WaitAsync(token);
            await abandoned.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(new byte[] { 4, 5, 6 }, await capture.ReadFrameAsync(token));
            Assert.Equal(new byte[] { 7, 8, 9 }, await capture.ReadPngScreencastAsync(token));

            // 停止推帧时还有一个待读取操作。切换模式时应保留原页面，
            // 并在重新推帧前丢弃旧帧。
            Assert.Equal(new byte[] { 7, 8, 9 }, await capture.ReadPngScreencastAsync(token, TimeSpan.Zero));
            await capture.StopScreencastAsync(token);
            Assert.Equal(new byte[] { 10 }, await capture.ReadFrameAsync(token));
            Assert.Equal(new byte[] { 11 }, await capture.ReadJpegScreencastAsync(token));
            await Assert.ThrowsAsync<IOException>(() => capture.StopScreencastAsync(token));
            Assert.Equal(new byte[] { 12 }, await capture.ReadFrameAsync(token));
            await peer;
        }
        finally
        {
            await deadline.CancelAsync();
            await peer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        // 模拟浏览器端处理 DevTools 请求和响应。
        async Task RunBrowserAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            using var socket = await UpgradeAsync(connection, token);

            int failedStart = await ReadRequestAsync("Page.startScreencast");
            await SendAsync(new { id = failedStart, error = new { message = "Temporary failure" } });
            int start = await ReadRequestAsync("Page.startScreencast");
            await SendAsync(Frame(sessionId: 1, "AQID"));
            await ReadRequestAsync("Page.screencastFrameAck");
            await SendAsync(new { id = start, result = new { } });

            int first = await ReadRequestAsync("Page.captureScreenshot");
            screenshotReceived.SetResult();
            int second = await ReadRequestAsync("Page.captureScreenshot");
            await SendAsync(new { id = first, result = new { data = "AA==" } });
            await SendAsync(new { id = second, result = new { data = "BAUG" } });
            await SendAsync(Frame(sessionId: 2, "BwgJ"));
            await ReadRequestAsync("Page.screencastFrameAck");

            int stop = await ReadRequestAsync("Page.stopScreencast");
            await SendAsync(Frame(sessionId: 3, "AA=="));
            await ReadRequestAsync("Page.screencastFrameAck");
            await SendAsync(new { id = stop, result = new { } });
            int fallback = await ReadRequestAsync("Page.captureScreenshot");
            await SendAsync(new { id = fallback, result = new { data = "Cg==" } });
            int restarted = await ReadRequestAsync("Page.startScreencast");
            await SendAsync(new { id = restarted, result = new { } });
            await SendAsync(Frame(sessionId: 4, "Cw=="));
            await ReadRequestAsync("Page.screencastFrameAck");
            int refused = await ReadRequestAsync("Page.stopScreencast");
            await SendAsync(new { id = refused, error = new { message = "No active screencast" } });
            int last = await ReadRequestAsync("Page.captureScreenshot");
            await SendAsync(new { id = last, result = new { data = "DA==" } });

            // 构造测试使用的浏览器推帧事件。
            static object Frame(int sessionId, string data) =>
                new { method = "Page.screencastFrame", @params = new { sessionId, data } };

            // 从测试 WebSocket 读取一条 DevTools 请求。
            async Task<int> ReadRequestAsync(string method)
            {
                byte[] bytes = new byte[1024];
                using var request = new MemoryStream();
                ValueWebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(bytes.AsMemory(), token);
                    Assert.Equal(WebSocketMessageType.Text, part.MessageType);
                    request.Write(bytes, 0, part.Count);
                } while (!part.EndOfMessage);
                using var json = JsonDocument.Parse(request.ToArray());
                Assert.Equal(method, json.RootElement.GetProperty("method").GetString());
                return json.RootElement.GetProperty("id").GetInt32();
            }

            // 通过测试 WebSocket 发送模拟浏览器的 JSON 消息。
            ValueTask SendAsync(object value) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(),
                WebSocketMessageType.Text, endOfMessage: true, token);
        }
    }

    [Fact]
    public async Task OversizedFragmentedDevToolsMessageFailsWaitingScreenshot()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CancellationToken token = deadline.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task peer = SendOversizedReplyAsync();
        try
        {
            await using var capture = new HtmlCapture(maximumMessageBytes: 1024);
            await capture.ConnectSocketAsync(new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"), token);
            var error = await Assert.ThrowsAsync<IOException>(() => capture.ReadFrameAsync(token).AsTask());
            Assert.Contains("exceeds 1024 bytes", error.Message);
            await peer;
        }
        finally
        {
            await deadline.CancelAsync();
            await peer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        async Task SendOversizedReplyAsync()
        {
            using var connection = await listener.AcceptTcpClientAsync(token);
            using var socket = await UpgradeAsync(connection, token);
            await socket.ReceiveAsync(new byte[1024].AsMemory(), token);
            byte[] fragment = Encoding.UTF8.GetBytes(new string(' ', 600));
            await socket.SendAsync(fragment.AsMemory(), WebSocketMessageType.Text, endOfMessage: false, token);
            await socket.SendAsync(fragment.AsMemory(), WebSocketMessageType.Text, endOfMessage: false, token);
        }
    }

    private static async Task<WebSocket> UpgradeAsync(TcpClient connection, CancellationToken token)
    {
        var stream = connection.GetStream();
        using var headers = new StreamReader(stream, leaveOpen: true);
        string? key = null;
        while (await headers.ReadLineAsync(token) is { Length: > 0 } line)
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                key = line.Split(':', 2)[1].Trim();
        Assert.NotNull(key);
        string accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), token);
        return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, Timeout.InfiniteTimeSpan);
    }
}
