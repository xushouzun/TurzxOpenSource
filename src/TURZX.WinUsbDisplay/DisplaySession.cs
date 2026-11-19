using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TURZX.WinUsbDisplay;

/// <summary>表示已打开的 Win207 设备会话。所有传输停止后再释放会话。</summary>
public sealed class DisplaySession : IDisposable
{
    /// <summary>Win207 请求头长度，单位为字节。</summary>
    private const int HeaderSize = 512;
    /// <summary>串行化设备命令和应答读取的锁。</summary>
    private readonly Lock gate = new();
    /// <summary>防止多个视频流同时占用同一会话。</summary>
    private readonly Lock videoGate = new();
    /// <summary>当前会话持有的 WinUSB 传输实例。</summary>
    private readonly WinUsbTransport transport;
    /// <summary>会话独立持有的固件命令密钥副本。</summary>
    private readonly byte[] key;
    /// <summary>按设备型号限制亮度的上限。</summary>
    private readonly byte brightnessLimit;
    // 沿用原版 Win207 控制程序的主机时间戳基准。
    /// <summary>将主机时间转换为 Win207 时间戳时使用的纪元。</summary>
    private readonly DateTime timestampEpoch = DateTime.Today.AddDays(-1);
    /// <summary>记录当前会话是否已释放。</summary>
    private bool disposed;

    private readonly ushort productId;
    private static readonly Lazy<byte[]> Native0123ClearOverlay = new(() =>
    {
        DisplayModel model = DisplayModel.Known.Single(model => model.ProductId == 0x0123);
        return RgbaPng.CreateTransparent(model.Width, model.Height);
    });

    /// <summary>打开独占设备会话，并保存命令密钥和型号。</summary>
    internal DisplaySession(string path, ReadOnlySpan<byte> commandKey, ushort productId)
    {
        if (commandKey.Length != 8)
            throw new ArgumentException("The firmware command key must be 8 bytes.", nameof(commandKey));

        this.productId = productId;
        brightnessLimit = productId == 0x0123 ? (byte)80 : (byte)100;
        key = commandKey.ToArray();
        try
        {
            using var des = DES.Create();
            des.Key = key;
            transport = new WinUsbTransport(path);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    /// <summary>读取设备返回的 UTF-8 固件版本。</summary>
    public string ReadFirmwareVersion() => ReadFirmwareVersion(CancellationToken.None);

    /// <summary>读取固件版本，并在 USB 请求之间检查取消状态。</summary>
    public string ReadFirmwareVersion(CancellationToken cancellation)
    {
        byte[] reply = ReadVerifiedQueryResponse(10, cancellation);
        return Encoding.UTF8.GetString(reply.AsSpan(8, 32)).TrimEnd('\0');
    }

    /// <summary>设置 0 至 100 的亮度百分比，实际值受型号上限限制。</summary>
    public void SetBrightness(byte percent)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, (byte)100);
        Command(14, (byte)(percent * brightnessLimit / 100));
    }

    /// <summary>设置显示方向，取值 0 至 3，每增加 1 表示再旋转 90 度。</summary>
    public void SetRotation(byte quarterTurns)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quarterTurns, (byte)3);
        Command(13, quarterTurns);
    }

    /// <summary>停止设备上的主机视频流。开始新场景前应取消正在运行的 PlayVideo 调用。</summary>
    public void StopVideo() => Command(123);

    /// <summary>停止主机视频流和时钟图层，再显示不超过 <see cref="MaxEncodedFrameBytes"/> 字节的 8 位 RGBA PNG 或 JPEG。</summary>
    public void ShowImage(ReadOnlySpan<byte> image) =>
        ShowImage(image, GetImageCommand(image), CancellationToken.None);

    private void ShowImage(ReadOnlySpan<byte> image, byte command, CancellationToken cancellation)
    {
        lock (videoGate)
        lock (gate)
        {
            SendVerifiedImageCommand(123, [], cancellation);
            SendVerifiedImageCommand(52, [], cancellation);
            if (productId == 0x0123 && command == 101)
                ClearOverlayWithoutResponse(Native0123ClearOverlay.Value, cancellation);
            if (command == 101)
                SendVerifiedImageCommand(command, image, cancellation);
            else
                Exchange(command, image);
        }
    }

    private void SendVerifiedImageCommand(byte command, ReadOnlySpan<byte> image, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ReadMatchingReply(command, 2, Exchange(command, image), () => transport.ReadResponse(), cancellation);
    }

    /// <summary>显示静态图片并每秒刷新一次，直到取消。</summary>
    public void PlayImage(ReadOnlyMemory<byte> image, CancellationToken cancellation, Action? onStarted = null)
        => PlayImage(image, 1, cancellation, onStarted);

    /// <summary>以指定帧率持续发送同一张图片，直到取消。</summary>
    public void PlayImage(ReadOnlyMemory<byte> image, byte fps, CancellationToken cancellation,
        Action? onStarted = null, Action? onFrameSent = null)
        => PlayImage(image, fps, cancellation, onStarted, onFrameSent, null);

    /// <summary>持续发送图片，并记录首帧之后的 USB 写入和应答耗时。</summary>
    public void PlayImage(ReadOnlyMemory<byte> image, byte fps, CancellationToken cancellation,
        Action? onStarted, Action? onFrameSent, Action<TimeSpan, TimeSpan>? onTransfer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, (byte)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, (byte)60);
        byte command = GetImageCommand(image.Span);
        lock (videoGate)
        {
            cancellation.ThrowIfCancellationRequested();
            ShowImage(image.Span, command, cancellation);
            byte[] packet = new byte[checked(HeaderSize + image.Length)];
            image.Span.CopyTo(packet.AsSpan(HeaderSize));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double interval = (double)System.Diagnostics.Stopwatch.Frequency / fps;
            double nextFrame = interval;
            WaitHandle cancelSignal = cancellation.WaitHandle;
            using var frameTimer = ImageFrameTimer.TryCreate();
            WaitHandle[]? waitHandles = frameTimer is null ? null : [cancelSignal, frameTimer];
            onFrameSent?.Invoke();
            onStarted?.Invoke();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                long elapsed = clock.ElapsedTicks;
                if (nextFrame < elapsed)
                    nextFrame += Math.Floor((elapsed - nextFrame) / interval) * interval;
                double waitMs = (nextFrame - elapsed) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                // 同步送屏线程直接等待取消信号，避免每帧依赖线程池唤醒。
                if (waitMs > 0)
                {
                    if (waitHandles is not null && frameTimer!.Arm(waitMs))
                        WaitHandle.WaitAny(waitHandles);
                    else
                        cancelSignal.WaitOne((int)Math.Ceiling(waitMs));
                }
                cancellation.ThrowIfCancellationRequested();
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    EncodeHeader(packet, key,
                        unchecked((uint)Convert.ToInt64((DateTime.UtcNow - timestampEpoch).TotalMilliseconds)),
                        command, image.Length, 0, false, default, true);
                    transport.Exchange(packet, packet.Length, discardReply: true, onTransfer: onTransfer);
                }
                onFrameSent?.Invoke();
                nextFrame += interval;
            }
        }
    }

    /// <summary>为连续图像送屏提供相对时间等待。</summary>
    private sealed class ImageFrameTimer : WaitHandle
    {
        /// <summary>尝试创建高精度等待定时器。</summary>
        internal static ImageFrameTimer? TryCreate()
        {
            if (!OperatingSystem.IsWindows() || Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                return null;

            // 高精度等待定时器按相对期限唤醒，无需更改进程的计时器分辨率。
            // 创建高精度 Windows 定时器所用的标志。
            const uint highResolution = 0x2;
            // 等待并设置定时器所需的访问权限。
            const uint synchronizeAndModify = 0x00100002;
            var handle = CreateWaitableTimerExW(0, 0, highResolution, synchronizeAndModify);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            return new ImageFrameTimer { SafeWaitHandle = handle };
        }

        /// <summary>按指定毫秒数设置下一次唤醒。</summary>
        internal bool Arm(double milliseconds)
        {
            long dueTime = -(long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond);
            return SetWaitableTimer(SafeWaitHandle, in dueTime, 0, 0, 0, false);
        }

        /// <summary>通过 Windows API 创建可等待定时器。</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeWaitHandle CreateWaitableTimerExW(
            nint attributes, nint name, uint flags, uint access);

        /// <summary>通过 Windows API 设置定时器的到期时间。</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern bool SetWaitableTimer(Microsoft.Win32.SafeHandles.SafeWaitHandle timer,
            in long dueTime, int period, nint completion, nint argument, bool resume);
    }

    /// <summary>按图片格式选择设备显示命令。</summary>
    /// <summary>单张已编码送屏图片允许的最大字节数。</summary>
    public const int MaxEncodedFrameBytes = 950_000;

    internal static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10];
    internal static ReadOnlySpan<byte> JpegSignature => [0xff, 0xd8, 0xff];

    private static byte GetImageCommand(ReadOnlySpan<byte> image)
    {
        if (image.StartsWith(PngSignature))
        {
            ValidateOverlay(image);
            return 102;
        }

        ValidateFrameSize(image.Length);
        return image.StartsWith(JpegSignature) ? (byte)101 : throw new InvalidDataException("Expected a PNG or JPEG image.");
    }

    /// <summary>验证送屏 PNG 的大小、8 位 RGBA 格式、块校验和及压缩像素数据。</summary>
    /// <param name="png">完整的 PNG 数据。</param>
    /// <exception cref="InvalidDataException">数据不符合送屏 PNG 要求。</exception>
    public static void ValidateOverlay(ReadOnlySpan<byte> png)
    {
        ValidateOverlayStructure(png);
        RgbaPng.ValidateImageData(png);
    }

    private static void ValidateOverlayStructure(ReadOnlySpan<byte> png)
    {
        ValidateFrameSize(png.Length);
        if (png.Length < 33 || !png.StartsWith(PngSignature) ||
            BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(png[16..]) == 0 || BinaryPrimitives.ReadUInt32BigEndian(png[20..]) == 0 ||
            png[24] != 8 || png[25] != 6)
            throw new InvalidDataException("The display requires an 8-bit RGBA PNG.");

        ReadOnlySpan<byte> remaining = png[33..];
        bool hasImageData = false;
        while (remaining.Length >= 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(remaining);
            if (length > remaining.Length - 12)
                break;

            ReadOnlySpan<byte> type = remaining.Slice(4, 4);
            remaining = remaining[((int)length + 12)..];
            if (type.SequenceEqual("IDAT"u8))
                hasImageData |= length != 0;
            else if (type.SequenceEqual("IEND"u8))
            {
                if (hasImageData && length == 0 && remaining.IsEmpty)
                    return;
                break;
            }
        }

        throw new InvalidDataException("The PNG must contain complete image data and an end chunk.");
    }

    private static void ValidateFrameSize(int length)
    {
        if (length is < 1 or > MaxEncodedFrameBytes)
            throw new InvalidDataException($"Display images must be between 1 and {MaxEncodedFrameBytes} bytes.");
    }

    /// <summary>将不超过 <see cref="MaxEncodedFrameBytes"/> 字节的 8 位 RGBA PNG 显示为前景，保留 H.264 背景。</summary>
    public void ShowOverlay(ReadOnlySpan<byte> png)
    {
        ValidateOverlay(png);
        Exchange(102, png, discardReply: true);
    }

    /// <summary>发送已由捕捉端编码或检查的 PNG，避免在发送线程重复解压逐帧数据。</summary>
    internal void SendEncodedOverlay(ReadOnlySpan<byte> png)
    {
        ValidateOverlayStructure(png);
        Exchange(102, png, discardReply: true);
    }

    /// <summary>为不应答 PNG 的固件写入清层帧，并在同一个锁内留出处理时间。</summary>
    internal void ClearOverlayWithoutResponse(ReadOnlySpan<byte> png, CancellationToken cancellation)
    {
        ValidateOverlayStructure(png);
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            Exchange(102, png, waitForResponse: false);
            Delay(100, cancellation);
        }
    }

    /// <summary>发送一帧 JPEG，适用于没有视频背景的连续输出。</summary>
    public void ShowJpegFrame(ReadOnlySpan<byte> jpeg) => ShowJpegFrame(jpeg, null);

    /// <summary>发送 JPEG 帧，并可记录 USB 写入和等待应答的耗时。</summary>
    public void ShowJpegFrame(ReadOnlySpan<byte> jpeg, Action<TimeSpan, TimeSpan>? onTransfer)
    {
        if (GetImageCommand(jpeg) != 101)
            throw new InvalidDataException("Expected a JPEG frame.");
        Exchange(101, jpeg, discardReply: true, onTransfer: onTransfer);
    }

    /// <summary>停止主机串流及设备存储中的媒体播放。</summary>
    public void StopPlayback()
    {
        lock (gate)
            StopPlaybackLayers(StopVideo, StopLocalVideo, StopImageLoop);
    }

    internal static void StopPlaybackLayers(params Action[] stopLayers)
    {
        List<Exception>? failures = null;
        foreach (Action stop in stopLayers)
        {
            try
            {
                stop();
            }
            catch (Exception error) when (error is IOException or Win32Exception)
            {
                if (error is Win32Exception { NativeErrorCode: 6 or 1167 })
                    throw;
                (failures ??= []).Add(error);
            }
        }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
        if (failures is { Count: > 1 })
            throw new AggregateException("Could not stop all playback layers.", failures);
    }

    /// <summary>读取 SD 卡和内部闪存容量，单位为 KiB。</summary>
    public StorageCapacity ReadStorageCapacity() => ReadStorageCapacity(CancellationToken.None);

    /// <summary>读取以 KiB 为单位的存储容量，并在 USB 请求之间检查取消状态。</summary>
    public StorageCapacity ReadStorageCapacity(CancellationToken cancellation)
    {
        var reply = ReadVerifiedQueryResponse(100, cancellation);
        return new StorageCapacity(ReadVolume(8), ReadVolume(20));

        // 从应答的指定偏移读取一个存储卷的三个容量字段。
        StorageVolume ReadVolume(int offset) => new(
            BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(offset)),
            BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(offset + 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(offset + 8)));
    }

    /// <summary>在支持电压查询的固件上读取供电电压，单位为毫伏。</summary>
    public uint ReadSupplyVoltageMillivolts() => ReadSupplyVoltageMillivolts(CancellationToken.None);

    /// <summary>读取以毫伏为单位的供电电压，并在 USB 请求之间检查取消状态。</summary>
    public uint ReadSupplyVoltageMillivolts(CancellationToken cancellation)
    {
        var reply = ReadVerifiedQueryResponse(50, cancellation);
        return BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8));
    }

    /// <summary>写入 Win207 的持久化启动配置。</summary>
    public void SetStartupConfiguration(byte brightness, byte startupMode, byte rotation,
        bool keepAwakeOffline, byte offlineClockRotation)
    {
        byte[] arguments = EncodeStartupConfiguration(brightness, brightnessLimit, startupMode, rotation,
            keepAwakeOffline, offlineClockRotation);
        ExchangeMatchingCommand(125, arguments, [], 2, CancellationToken.None);
    }

    /// <summary>按固件字段顺序生成持久化启动配置。</summary>
    internal static byte[] EncodeStartupConfiguration(byte brightness, byte brightnessLimit, byte startupMode,
        byte rotation, bool keepAwakeOffline, byte offlineClockRotation)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(brightness, (byte)100);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startupMode, (byte)2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rotation, (byte)3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offlineClockRotation, (byte)3);

        // 原版 Win207 界面隐藏了休眠定时器，新配置使用零值。
        return [(byte)(brightness * brightnessLimit / 100), startupMode, 0, rotation, 0,
            keepAwakeOffline ? (byte)1 : (byte)0, offlineClockRotation];
    }

    /// <summary>请求重启显示设备的固件。</summary>
    public void RestartDevice() => Command(11);

    /// <summary>向设备发送删除指定条目的请求。</summary>
    public void DeleteDeviceFile(string path)
    {
        byte[] arguments = EncodeDevicePath(path);
        if (path.EndsWith('/'))
            throw new ArgumentException("Expected a file path.", nameof(path));
        ExchangeMatchingCommand(42, arguments, [], 2);
    }

    /// <summary>列出显示设备为指定目录返回的文件名。</summary>
    public IReadOnlyList<string> ListDeviceFiles(string directory) => ReadDeviceDirectory(directory).Files;

    /// <summary>读取目录，并标明是否已收到完整且包含目录信息的应答。</summary>
    public (IReadOnlyList<string> Files, bool IsComplete) ReadDeviceDirectory(string directory) =>
        ReadDeviceDirectory(directory, CancellationToken.None);

    /// <summary>读取目录，并在 USB 请求之间检查取消状态。</summary>
    public (IReadOnlyList<string> Files, bool IsComplete) ReadDeviceDirectory(string directory, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        byte[] arguments = EncodeDevicePath(directory);
        (byte[] Response, bool IsComplete) replies;
        lock (gate)
            replies = CollectDirectoryReplies(directory, () => ExchangeCommand(99, arguments), cancellation);

        cancellation.ThrowIfCancellationRequested();
        return (ParseDeviceFileList(Encoding.UTF8.GetString(replies.Response), replies.IsComplete), replies.IsComplete);
    }

    /// <summary>收集目录的分段应答，并标明是否完整。</summary>
    internal static (byte[] Response, bool IsComplete) CollectDirectoryReplies(string directory, Func<byte[]> request,
        CancellationToken cancellation)
    {
        using var response = new MemoryStream();
        string text = "";
        int matchingStart = -1;
        // 固件最多用 20 个 512 字节的应答返回目录列表。
        for (int i = 0; i < 20; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                byte[] packet = request();
                // 已知的控制和图像回包可能滞留在 IN 端点，不能拼入目录文本。
                if (IsNonDirectoryReply(packet))
                    continue;
                response.Write(packet);
            }
            catch (DeviceResponseException ex) when (ex.Failure == DeviceResponseFailure.NoData)
            {
                cancellation.ThrowIfCancellationRequested();
                if (response.Length == 0)
                    throw;
                break;
            }

            cancellation.ThrowIfCancellationRequested();
            text = Encoding.UTF8.GetString(response.GetBuffer(), 0, (int)response.Length).Replace("\0", "");
            if (IsEmptyDirectoryStatus(text) || text == "result:nodir-createfailed")
                return (Encoding.UTF8.GetBytes(text), true);

            matchingStart = FindRequestedDirectory(text, directory);
            if (matchingStart >= 0 && IsDirectoryReplyComplete(text, matchingStart))
                return (Encoding.UTF8.GetBytes(text[matchingStart..]), true);
        }

        cancellation.ThrowIfCancellationRequested();
        if (matchingStart >= 0)
            return (Encoding.UTF8.GetBytes(text[matchingStart..]), false);
        if (text.Length == 0)
            return (response.ToArray(), false);

        throw new DeviceResponseException(DeviceResponseFailure.UnexpectedReply,
            "The display did not return a listing for the requested directory.", response: response.ToArray());
    }

    private static bool IsNonDirectoryReply(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 512 || packet is not [_, 200, _, _, _, _, 0, 0, ..])
            return false;

        // 0xC8 也可能是文件名中 UTF-8 字符的起始字节。保留可能属于文本的分片。
        if (packet[2] is >= 0x80 and <= 0xbf && !packet.Slice(2, 4).ContainsAnyInRange((byte)1, (byte)31))
            return false;

        int end = packet[0] switch
        {
            42 or 52 or 101 or 102 or 123 => 8,
            98 => 12, // 文件大小是应答偏移 8 的四字节小端字段。
            _ => 0
        };
        return end != 0 && !packet[end..].ContainsAnyExcept((byte)0);
    }

    private static int FindRequestedDirectory(string text, string directory)
    {
        ReadOnlySpan<char> expected = directory.AsSpan().TrimEnd('/');
        for (int marker = text.IndexOf("file:", StringComparison.Ordinal); marker >= 0;
            marker = text.IndexOf("file:", marker + "file:".Length, StringComparison.Ordinal))
        {
            int header = text.LastIndexOf("dir:", marker, StringComparison.Ordinal);
            if (header >= 0)
            {
                var path = text.AsSpan(header + "dir:".Length, marker - header - "dir:".Length);
                if (path.StartsWith("/") && path.TrimEnd('/').SequenceEqual(expected))
                    return header;
            }
        }
        return -1;
    }

    private static bool IsDirectoryReplyComplete(string text, int start)
    {
        int marker = text.IndexOf("file:", start, StringComparison.Ordinal);
        return FindNextDirectoryListing(text, marker).RepeatsDirectory;
    }

    private static (int Boundary, bool RepeatsDirectory) FindNextDirectoryListing(string text, int firstMarker)
    {
        int entries = firstMarker + "file:".Length;
        int nextMarker = text.IndexOf("file:", entries, StringComparison.Ordinal);
        if (nextMarker < 0)
            return (-1, false);

        // 只使用首个后续列表作为边界。其他目录的元数据不能成为文件条目，也不能证明首个列表完整。
        int nextHeader = text.AsSpan(entries, nextMarker - entries).LastIndexOf("dir:".AsSpan(), StringComparison.Ordinal);
        if (nextHeader < 0)
            return (nextMarker, false);

        int nextStart = entries + nextHeader;
        int firstHeader = text.LastIndexOf("dir:", firstMarker, StringComparison.Ordinal);
        bool repeats = firstHeader >= 0 && text.AsSpan(firstHeader, firstMarker - firstHeader)
            .SequenceEqual(text.AsSpan(nextStart, nextMarker - nextStart));
        const string prefix = "result:";
        int boundary = text.AsSpan(entries, nextStart - entries).EndsWith(prefix, StringComparison.Ordinal)
            ? nextStart - prefix.Length : nextStart;
        return (boundary, repeats);
    }

    private static bool IsEmptyDirectoryStatus(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return false;

        const string status = "nodir-createdone";
        const string prefixed = "result:" + status;
        const string existingEmpty = "result:dir:file:";
        while (!text.IsEmpty)
        {
            if (text.StartsWith(existingEmpty, StringComparison.Ordinal))
                text = text[existingEmpty.Length..];
            else if (text.StartsWith(prefixed, StringComparison.Ordinal))
                text = text[prefixed.Length..];
            else if (text.StartsWith(status, StringComparison.Ordinal))
                text = text[status.Length..];
            else
                return false;
        }
        return true;
    }

    /// <summary>播放已存储在显示设备上的媒体文件。</summary>
    public void PlayDeviceFile(string path)
    {
        byte[] arguments = EncodeDevicePath(path);
        byte command = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".h264" or ".264" or ".mp4" => 110,
            ".jpg" or ".jpeg" or ".png" or ".bmp" => 113,
            _ => throw new ArgumentException("Unsupported on-device media format.", nameof(path))
        };

        lock (videoGate)
        lock (gate)
        {
            StopPlayback();
            ShowDefaultClock(false);
            byte[] reply = ExchangeMatchingCommand(command, arguments, [], command == 110 ? 9 : 2, CancellationToken.None);
            if (!IsDeviceFilePlaybackAccepted(command, reply, () =>
                    ExchangeMatchingCommand(112, [], [], 9, CancellationToken.None)))
                throw new DeviceResponseException(DeviceResponseFailure.UnexpectedReply, "The display rejected the media file.", response: reply);
        }
    }

    internal static bool IsDeviceFilePlaybackAccepted(byte command, byte[] reply, Func<byte[]> readPlaybackState)
    {
        if (reply.Length == 0 || reply[0] != command)
            return false;

        int statusOffset = command == 110 ? 8 : 1;
        if (reply.Length > statusOffset && reply[statusOffset] == 200)
            return true;

        // 部分固件将 110 的确认码放在偏移 1。通过独立的只读状态查询确认播放。
        if (command != 110 || reply.Length < 9 || reply[1] != 200 || reply[8] != 0)
            return false;

        byte[] state = readPlaybackState();
        return state.Length >= 9 && state[0] == 112 && state[8] != 0;
    }

    /// <summary>将设备文件路径转换为请求参数。</summary>
    internal static byte[] EncodeDevicePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path[0] != '/' || path.Length > 484 || path.Any(c => c is < ' ' or > '~' or '\\')
            || path.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Expected an absolute ASCII display path of at most 484 bytes.", nameof(path));

        var arguments = new byte[8 + path.Length];
        BinaryPrimitives.WriteInt32BigEndian(arguments, path.Length);
        Encoding.ASCII.GetBytes(path, arguments.AsSpan(8));
        return arguments;
    }

    // 固件可能用 NUL 填充目录回包，或重复返回文件列表。
    // 全填充回包按无条目处理。无法识别的非空回包仍视为错误。
    /// <summary>解析目录应答中的文件名，并处理重复或不完整的记录。</summary>
    internal static IReadOnlyList<string> ParseDeviceFileList(string reply, bool isComplete = true)
    {
        const string marker = "file:";
        string text = reply.Replace("\0", "");
        if (text.Length == 0 || IsEmptyDirectoryStatus(text))
            return [];

        if (text.StartsWith("result:nodir-createfailed", StringComparison.Ordinal))
            throw new DeviceResponseException(DeviceResponseFailure.DirectoryCreationFailed,
                "The display could not create the requested directory.", response: Encoding.UTF8.GetBytes(reply));

        int start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            throw new DeviceResponseException(DeviceResponseFailure.UnexpectedReply, "The display returned an unrecognized directory listing.");

        (int boundary, _) = FindNextDirectoryListing(text, start);
        int header = text.LastIndexOf("dir:", start, StringComparison.Ordinal);
        int headerLength = header < 0 ? 0 : start - header;
        start += marker.Length;
        if (boundary >= 0)
            text = text[..boundary];
        if (!isComplete)
        {
            if (headerLength > 0)
            {
                int repeatedHeader = text.AsSpan(start).IndexOf(text.AsSpan(header, headerLength), StringComparison.Ordinal);
                if (repeatedHeader >= 0)
                    text = text[..(start + repeatedHeader)];
            }
            // 最后一条记录可能被拆分到多个 USB 应答中。
            int separator = text.LastIndexOf('/');
            if (separator < start)
                return [];
            text = text[..separator];
        }
        return text[start..].Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(name => name is not ("." or "..") && !name.Any(c => c == '\\' || char.IsControl(c)))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>停止设备本地的 JPEG 循环播放。</summary>
    public void StopImageLoop() => Command(114);

    /// <summary>用主机本地时间设置并启用设备时钟，或关闭时钟图层。</summary>
    public void ShowDefaultClock(bool enabled)
    {
        if (!enabled)
        {
            Command(52);
            return;
        }

        WriteClock(1);
    }

    /// <summary>同步固件时钟，无需启用时钟图层。</summary>
    public void SynchronizeClock() => WriteClock(2);

    /// <summary>以指定操作模式写入当前主机本地时间。</summary>
    private void WriteClock(byte mode)
    {
        DateTime now = DateTime.Now;
        byte[] time = [(byte)(now.Year >> 8), (byte)now.Year, (byte)now.Month, (byte)now.Day,
            (byte)now.Hour, (byte)now.Minute, (byte)now.Second, mode];
        Exchange(51, [], arguments: time);
    }

    /// <summary>执行允许列表中的诊断查询，返回设备的原始应答。</summary>
    public byte[] ReadDiagnostic(byte command, bool raw = false)
    {
        if (!raw && command is (10 or 17 or 50 or 60 or 100 or 112 or 122 or 201))
            return Command(command);
        if (raw && command is (0x60 or 0x62))
            return ExchangeRawCommand(command);
        throw new ArgumentOutOfRangeException(nameof(command), "Unknown diagnostic command.");
    }

    /// <summary>设置设备是否等待视频同步时序。</summary>
    public void SetVideoClockSync(bool enabled) => Command(49, enabled ? (byte)1 : (byte)0);

    /// <summary>设置设备的视频帧率，取值为 1 至 60。</summary>
    public void SetFrameRate(byte fps)
    {
        if (fps is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(fps));
        Command(15, fps);
    }

    /// <summary>停止播放设备中存储的 H.264 视频。</summary>
    public void StopLocalVideo() => Command(111);
    /// <summary>为兼容设备设置 Windows 桌面模式，设备重启后生效。此方法只发送 150 号指令。</summary>
    public void SwitchToDesktop() => Command(150);

    /// <summary>传输 Annex B 格式的 H.264 视频流，并按设备队列状态控制发送。这是阻塞操作，应在工作线程运行。</summary>
    /// <remarks>在读取中取消后，请换用新流重试。原流可能仍有一个未完成的操作。</remarks>
    public void PlayVideo(Stream h264, byte fps, bool loop = false, CancellationToken cancellation = default,
        Action? onStarted = null) => PlayVideo(h264, fps, loop, cancellation, onStarted, resetPlayback: true);

    /// <summary>播放 Annex B 格式的 H.264 视频。若已停止上次播放并发送首张前景帧，请将 resetPlayback 设为 false。</summary>
    public void PlayVideo(Stream h264, byte fps, bool loop, CancellationToken cancellation,
        Action? onStarted, bool resetPlayback) =>
        PlayVideo(h264, fps, loop, cancellation, onStarted, resetPlayback, int.MaxValue);

    /// <summary>发送 H.264，并允许实时流使用较小的传输块以降低缓冲延迟。</summary>
    public void PlayVideo(Stream h264, byte fps, bool loop, CancellationToken cancellation,
        Action? onStarted, bool resetPlayback, int maximumBlockBytes, Action<TimeSpan>? onQueueWait = null,
        Action<int>? onBlockAcknowledged = null)
    {
        ArgumentNullException.ThrowIfNull(h264);
        if (!h264.CanRead || (loop && !h264.CanSeek))
            throw new ArgumentException("Expected a readable Annex B H.264 stream. Looping requires seeking.", nameof(h264));
        ArgumentOutOfRangeException.ThrowIfZero(fps);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fps, (byte)60);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBlockBytes, 4);
        cancellation.ThrowIfCancellationRequested();

        // 先验证输入，再调整设备播放状态。不可定位流读掉的前缀要在首块补回。
        long start = 0;
        byte[]? prefix = null;
        if (h264.CanSeek)
            start = ValidateSeekableVideo(h264, cancellation);
        else
        {
            prefix = new byte[4];
            if (ReadVideoInput(h264, prefix, cancellation) != prefix.Length)
                throw new EndOfStreamException("The H.264 stream ended before its start code could be read.");
            ValidateAnnexBPrefix(prefix);
        }

        lock (videoGate)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (resetPlayback)
                {
                    Command(123);
                    Command(52);
                }
                Command(15, fps);
                // 由固件报告块大小，零值沿用旧固件的默认块大小，再套用调用方的传输上限。
                byte[] reply = ReadExpectedResponse(17, 12, cancellation);
                int blockSize = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(8));
                if (blockSize == 0)
                    blockSize = 198 * 1024;
                if (blockSize < 0 || blockSize > 4 * 1024 * 1024)
                    throw new IOException($"Unexpected H.264 block size: {blockSize}.");

                var block = new byte[Math.Min(blockSize, maximumBlockBytes)];
                WaitForQueue(1, cancellation);
                if (prefix is not null)
                {
                    if (maximumBlockBytes != int.MaxValue)
                    {
                        SendLiveVideoBlocks(h264, prefix, block, cancellation, (data, finalBlock) =>
                        {
                            // 设备确认首块后才通知上层播放已启动，后续按设备缓冲深度节流。
                            int queueDepth = ReadVideoBlockQueueDepth(Exchange(121, data.Span, finalBlock: finalBlock));
                            onBlockAcknowledged?.Invoke(queueDepth);
                            onStarted?.Invoke();
                            onStarted = null;
                            if (!finalBlock && queueDepth > 3)
                            {
                                long waitStarted = Stopwatch.GetTimestamp();
                                WaitForQueue(2, cancellation, 10);
                                onQueueWait?.Invoke(Stopwatch.GetElapsedTime(waitStarted));
                            }
                        });
                        return;
                    }

                    // 保留一个前瞻字节，以便在不可定位的流上正确标记末块。
                    int size = prefix.Length;
                    if (block.Length < size)
                        throw new IOException("The H.264 block is too small for the stream prefix.");
                    prefix.AsSpan().CopyTo(block);
                    var lookahead = new byte[1];
                    while (true)
                    {
                        size += ReadVideoInput(h264, block.AsMemory(size), cancellation);
                        int next = size == block.Length
                            ? ReadVideoInput(h264, lookahead, cancellation)
                            : 0;
                        cancellation.ThrowIfCancellationRequested();
                        reply = Exchange(121, block.AsSpan(0, size), finalBlock: next == 0);
                        int queueDepth = ReadVideoBlockQueueDepth(reply);
                        onBlockAcknowledged?.Invoke(queueDepth);
                        onStarted?.Invoke();
                        onStarted = null;
                        if (next == 0)
                            break;
                        Delay(30, cancellation);
                        if (queueDepth > 3)
                        {
                            long waitStarted = Stopwatch.GetTimestamp();
                            WaitForQueue(2, cancellation);
                            onQueueWait?.Invoke(Stopwatch.GetElapsedTime(waitStarted));
                        }
                        block[0] = lookahead[0];
                        size = 1;
                    }
                    return;
                }
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    // 每轮从输入原始位置重播，并重新读取当前长度以确定末块。
                    long remaining = RunVideoInputAsync(() =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        long length = h264.Length;
                        h264.Position = start;
                        return Task.FromResult(length - start);
                    }, cancellation);
                    if (remaining <= 0)
                        throw new EndOfStreamException("The H.264 stream no longer contains data at its starting position.");
                    while (remaining > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int size = (int)Math.Min(block.Length, remaining);
                        remaining -= RunVideoInputAsync(async () =>
                        {
                            await h264.ReadExactlyAsync(block.AsMemory(0, size), cancellation).ConfigureAwait(false);
                            return size;
                        }, cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        bool finalBlock = remaining == 0;
                        reply = Exchange(121, block.AsSpan(0, size), finalBlock: finalBlock);
                        int queueDepth = ReadVideoBlockQueueDepth(reply);
                        onBlockAcknowledged?.Invoke(queueDepth);
                        onStarted?.Invoke();
                        onStarted = null;
                        Delay(30, cancellation);
                        if (queueDepth > 3)
                            WaitForQueue(2, cancellation);
                    }
                } while (loop);
            }
            catch (Exception failure)
            {
                try
                {
                    StopVideo();
                }
                catch (Exception stopFailure) when (stopFailure is IOException or Win32Exception)
                {
                    if (cancellation.IsCancellationRequested)
                        throw new PlaybackStopException(failure is OperationCanceledException
                            ? stopFailure : new AggregateException(stopFailure, failure));
                    throw new AggregateException(failure, stopFailure);
                }
                throw;
            }
        }
    }

    /// <summary>校验可定位视频流的 Annex B 前缀并保留起始读取位置。</summary>
    internal static long ValidateSeekableVideo(Stream h264, CancellationToken cancellation) =>
        RunVideoInputAsync(async () =>
        {
            long start = h264.Position;
            byte[] prefix = new byte[4];
            try
            {
                await h264.ReadExactlyAsync(prefix, cancellation).ConfigureAwait(false);
                ValidateAnnexBPrefix(prefix);
                return start;
            }
            finally
            {
                h264.Position = start;
            }
        }, cancellation);

    // 自定义流也可能在调用 ReadAsync 前同步阻塞。取消时仅停止等待，后台操作仍独占该流和缓冲区。
    private static T RunVideoInputAsync<T>(Func<Task<T>> operation, CancellationToken cancellation) =>
        WaitForVideoRead(Task.Run(operation, cancellation), cancellation);

    /// <summary>检查输入是否以受支持的 Annex B 起始码开头。</summary>
    private static void ValidateAnnexBPrefix(ReadOnlySpan<byte> prefix)
    {
        if (!prefix.StartsWith<byte>([0, 0, 1]) && !prefix.SequenceEqual<byte>([0, 0, 0, 1]))
            throw new InvalidDataException("Expected Annex B H.264 start codes.");
    }

    /// <summary>读取视频数据，并在不可定位输入上支持取消等待。</summary>
    private static int ReadVideoInput(Stream stream, Memory<byte> buffer, CancellationToken cancellation)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            Memory<byte> remaining = buffer[total..];
            int count = RunVideoInputAsync(() => stream.ReadAsync(remaining, cancellation).AsTask(), cancellation);
            if (count == 0)
                break;
            total += count;
        }
        return total;
    }

    /// <summary>等待视频读取完成，取消时跟踪尚未结束的读取。</summary>
    private static T WaitForVideoRead<T>(Task<T> pending, CancellationToken cancellation)
    {
        try
        {
            return pending.WaitAsync(cancellation).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 部分 Stream 实现可能忽略取消请求。读取完成前，缓冲区由该读取操作独占。
            _ = ObserveReadAsync(pending);
            throw;
        }
    }

    /// <summary>观察取消后仍在运行的读取，以接收其最终异常。</summary>
    private static async Task ObserveReadAsync(Task read) =>
        await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>按大小和等待时间拆分实时视频数据，并标记最后一块。</summary>
    internal static void SendLiveVideoBlocks(Stream stream, ReadOnlySpan<byte> prefix, byte[] block,
        CancellationToken cancellation, Action<ReadOnlyMemory<byte>, bool> send)
    {
        if (block.Length < prefix.Length)
            throw new IOException("The H.264 block is too small for the stream prefix.");

        // 待完成的读取使用独立缓冲区，因此定时发送数据时可以让它继续读取。
        var incoming = new byte[Math.Min(block.Length, 16 * 1024)];
        prefix.CopyTo(block);
        int size = prefix.Length;
        bool first = true;
        int limit = Math.Min(block.Length, 4096);
        // 首块尽快启动播放。后续允许两帧左右合并，减少每块同步等待 USB 应答的开销。
        var startupWait = TimeSpan.FromMilliseconds(40);
        var playbackWait = TimeSpan.FromMilliseconds(100);
        long bufferedSince = Stopwatch.GetTimestamp();
        using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        CancellationToken readToken = readLifetime.Token;
        Task<int>? pending = null;
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (pending is null)
                {
                    Memory<byte> destination = incoming.AsMemory(0, Math.Min(incoming.Length, limit + 1 - size));
                    pending = Task.Run(() => stream.ReadAsync(destination, readToken).AsTask(), readToken);
                }

                int read;
                if (!pending.IsCompleted && size > (first ? prefix.Length : 1))
                {
                    TimeSpan remaining = (first ? startupWait : playbackWait) - Stopwatch.GetElapsedTime(bufferedSince);
                    try
                    {
                        read = pending.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, cancellation)
                            .GetAwaiter().GetResult();
                    }
                    catch (TimeoutException) when (!pending.IsFaulted)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        // 保留一个字节，以便下次读取遇到 EOF 时仍能发送非空的末尾数据块。
                        send(block.AsMemory(0, size - 1), false);
                        block[0] = block[size - 1];
                        size = 1;
                        first = false;
                        limit = block.Length;
                        continue;
                    }
                }
                else
                    read = pending.WaitAsync(cancellation).GetAwaiter().GetResult();
                pending = null;
                cancellation.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    send(block.AsMemory(0, size), true);
                    return;
                }

                if (size == 1)
                    bufferedSince = Stopwatch.GetTimestamp();
                // 每次最多读到块上限再加一个字节。多出的字节成为下一块的起点。
                int copied = Math.Min(read, limit - size);
                incoming.AsSpan(0, copied).CopyTo(block.AsSpan(size));
                size += copied;
                if (read > copied)
                {
                    send(block.AsMemory(0, size), false);
                    block[0] = incoming[copied];
                    size = 1;
                    first = false;
                    limit = block.Length;
                }
            }
        }
        finally
        {
            if (pending is not null)
            {
                readLifetime.Cancel();
                _ = ObserveReadAsync(pending);
            }
        }
    }

    /// <summary>等待设备的视频队列腾出空间。</summary>
    private void WaitForQueue(int target, CancellationToken cancellation, int pollMilliseconds = 50)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (ReadExpectedResponse(122, 9, cancellation)[8] <= target)
                return;
            if (timeout.Elapsed >= TimeSpan.FromSeconds(15))
                throw new TimeoutException("The display's H.264 queue did not drain.");
            Delay(pollMilliseconds, cancellation);
        }
    }

    /// <summary>从 H.264 块应答的偏移 8 读取队列深度。固件块应答的首字节不作命令回显校验。</summary>
    internal static int ReadVideoBlockQueueDepth(byte[] response) => response.Length >= 9
        ? response[8]
        : throw new DeviceResponseException(DeviceResponseFailure.UnexpectedReply,
            $"The display returned a short H.264 block acknowledgement ({response.Length} bytes; expected at least 9).",
            response: response);

    /// <summary>等待指定时长，期间响应取消。</summary>
    private static void Delay(int milliseconds, CancellationToken cancellation)
    {
        if (cancellation.WaitHandle.WaitOne(milliseconds))
            cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>查询固件、电压或存储信息，校验后返回原始应答，并重试临时读取故障。</summary>
    /// <param name="command">无参数的只读命令，可查询固件（10）、电压（50）或存储（100）。</param>
    /// <returns>设备返回的与命令匹配的原始应答字节。</returns>
    public byte[] ReadVerifiedQueryResponse(byte command) => ReadVerifiedQueryResponse(command, CancellationToken.None);

    /// <summary>查询并校验固件、电压或存储应答，在请求之间检查取消状态。</summary>
    public byte[] ReadVerifiedQueryResponse(byte command, CancellationToken cancellation) => command switch
    {
        10 => ReadExpectedResponse(command, 40, cancellation, static data => data[8] != 0),
        50 => ReadExpectedResponse(command, 12, cancellation),
        100 => ReadExpectedResponse(command, 32, cancellation),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    /// <summary>发送查询并校验应答的命令和最短长度。</summary>
    private byte[] ReadExpectedResponse(byte command, int minimumLength, CancellationToken cancellation,
        Func<byte[], bool>? validate = null) => RetryVerifiedQuery(gate, command, minimumLength,
            () => Exchange(command, []), () => transport.TryResetOutputPipe(), cancellation, validate,
            () => transport.ReadResponse());

    /// <summary>在同一个锁内查询、验证应答，并按需重试或复位输出管道。</summary>
    internal static byte[] RetryVerifiedQuery(Lock gate, byte command, int minimumLength, Func<byte[]> request,
        Action resetOutputPipe, CancellationToken cancellation, Func<byte[], bool>? validate = null,
        Func<byte[]>? readNextResponse = null)
    {
        byte[] reply = [];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (attempt > 0)
                Delay(100, cancellation);

            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    reply = request();
                    // 停止播放等命令可能留下旧应答。保持会话锁并继续读，避免重发查询让应答进一步积压。
                    // 每次最多跳过 20 个旧包；无匹配应答时仍按原来的三次查询上限报告失败。
                    for (int skipped = 0; readNextResponse is not null && reply.Length > 0
                        && reply[0] != command && skipped < 20; skipped++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        reply = readNextResponse();
                    }
                }
                catch (Exception ex) when (ex is IOException or Win32Exception { NativeErrorCode: 121 or 1460 })
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (attempt == 2)
                        throw;
                    // 写入超时后先恢复管道，再允许发送其他命令。
                    if (ex is Win32Exception)
                        resetOutputPipe();
                    continue;
                }
            }

            cancellation.ThrowIfCancellationRequested();
            if (reply.Length >= minimumLength && reply[0] == command && (validate is null || validate(reply)))
                return reply;
        }

        string lastResponse = reply.Length == 0 ? "no response bytes" : $"command {reply[0]}, {reply.Length} bytes";
        throw new DeviceResponseException(DeviceResponseFailure.UnexpectedReply,
            $"No matching response to command {command} after 3 attempts. Last response: {lastResponse}.", response: reply);
    }

    /// <summary>发送一条加密的 Win207 请求，返回从 IN 端点读到的原始字节。</summary>
    /// <param name="command">一字节的 Win207 命令 ID，可用十进制或十六进制表示。</param>
    /// <param name="arguments">最多 492 字节，从明文头部偏移 8 开始复制。调用方须显式填写协议规定的长度和标志位。</param>
    /// <param name="payload">追加在 512 字节 USB 头部之后的可选数据。</param>
    /// <returns>设备的原始应答。请查阅命令参考以解释状态和各字段。</returns>
    public byte[] ExchangeCommand(byte command, ReadOnlySpan<byte> arguments = default, ReadOnlySpan<byte> payload = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(arguments.Length, 492, nameof(arguments));
        return Exchange(command, payload, arguments: arguments, writePayloadLength: false);
    }

    /// <summary>只发送一次指令，跳过积压的其他命令应答，不重新执行控制操作。</summary>
    /// <param name="command">Win207 命令 ID，对应应答首字节。</param>
    /// <param name="arguments">从请求头偏移 8 开始写入的参数。</param>
    /// <param name="payload">追加在 USB 请求头之后的数据。</param>
    /// <param name="minimumLength">应答的最小字节数。</param>
    /// <param name="cancellation">在读取应答之间检查的取消令牌。</param>
    /// <returns>与命令编号匹配且达到最小长度的原始应答。</returns>
    public byte[] ExchangeMatchingCommand(byte command, byte[] arguments, byte[] payload,
        int minimumLength, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumLength, 1);
        lock (gate)
            return ReadMatchingReply(command, minimumLength, () => ExchangeCommand(command, arguments, payload),
                () => transport.ReadResponse(), cancellation);
    }

    internal static byte[] ReadMatchingReply(byte command, int minimumLength, Func<byte[]> send,
        Func<byte[]> readNext, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return ReadMatchingReply(command, minimumLength, send(), readNext, cancellation);
    }

    private static byte[] ReadMatchingReply(byte command, int minimumLength, byte[] reply,
        Func<byte[]> readNext, CancellationToken cancellation)
    {
        for (int skipped = 0; ; skipped++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (reply.Length >= minimumLength && reply.Length != 0 && reply[0] == command)
                return reply;
            if (skipped == 20)
                throw new DeviceResponseException(DeviceResponseFailure.UnexpectedReply,
                    $"No matching response for command {command}; skipped 20 stale packets.", response: reply);
            reply = readNext();
        }
    }

    /// <summary>发送一条未加密的 64 字节请求，返回从 IN 端点读到的原始字节。</summary>
    /// <param name="command">原始通道的命令字节，写在偏移 0。</param>
    /// <param name="arguments">最多 63 字节，从偏移 1 开始写入，其余字节填零。</param>
    /// <returns>设备的原始应答。</returns>
    public byte[] ExchangeRawCommand(byte command, ReadOnlySpan<byte> arguments = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(arguments.Length, 63, nameof(arguments));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var request = new byte[64];
            request[0] = command;
            arguments.CopyTo(request.AsSpan(1));
            return transport.Exchange(request);
        }
    }

    /// <summary>发送没有媒体负载的设备控制命令。</summary>
    private byte[] Command(byte command, byte value = 0, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            return Exchange(command, [], value);
        }
    }

    /// <summary>组装协议请求并在会话锁内与设备交换数据。</summary>
    private byte[] Exchange(byte command, ReadOnlySpan<byte> payload, byte value = 0, bool finalBlock = false,
        ReadOnlySpan<byte> arguments = default, bool writePayloadLength = true, bool discardReply = false,
        Action<TimeSpan, TimeSpan>? onTransfer = null, bool waitForResponse = true)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int length = checked(HeaderSize + payload.Length);
            byte[] packet = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                EncodePacketInto(packet.AsSpan(0, length), key,
                    unchecked((uint)Convert.ToInt64((DateTime.UtcNow - timestampEpoch).TotalMilliseconds)),
                    command, payload, value, finalBlock, arguments, writePayloadLength);
                if (waitForResponse)
                    return transport.Exchange(packet, length, discardReply, onTransfer);
                transport.Write(packet, length);
                return [];
            }
            finally
            {
                // 只清除本次使用的区域，再归还缓冲区。
                CryptographicOperations.ZeroMemory(packet.AsSpan(0, length));
                ArrayPool<byte>.Shared.Return(packet);
            }
        }
    }

    /// <summary>分配并生成包含命令头和负载的请求包。</summary>
    internal static byte[] EncodePacket(byte[] key, uint timestamp, byte command, ReadOnlySpan<byte> payload,
        byte value = 0, bool finalBlock = false, ReadOnlySpan<byte> arguments = default, bool writePayloadLength = true)
    {
        var packet = new byte[checked(HeaderSize + payload.Length)];
        EncodePacketInto(packet, key, timestamp, command, payload, value, finalBlock, arguments, writePayloadLength);
        return packet;
    }

    /// <summary>在调用方提供的缓冲区中写入请求包。</summary>
    private static void EncodePacketInto(Span<byte> packet, byte[] key, uint timestamp, byte command,
        ReadOnlySpan<byte> payload, byte value, bool finalBlock, ReadOnlySpan<byte> arguments, bool writePayloadLength)
    {
        EncodeHeader(packet, key, timestamp, command, payload.Length, value, finalBlock, arguments, writePayloadLength);
        payload.CopyTo(packet[HeaderSize..]);
    }

    /// <summary>填充并加密 Win207 请求头。</summary>
    private static void EncodeHeader(Span<byte> packet, byte[] key, uint timestamp, byte command,
        int payloadLength, byte value, bool finalBlock, ReadOnlySpan<byte> arguments, bool writePayloadLength)
    {
        Span<byte> header = stackalloc byte[500];
        header.Clear();
        header[0] = command;
        header[2] = 0x1a;
        header[3] = 0x6d;
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], timestamp);
        if (payloadLength == 0)
            header[8] = value;
        else if (writePayloadLength)
            BinaryPrimitives.WriteInt32BigEndian(header[8..], payloadLength);
        header[12] = finalBlock ? (byte)1 : (byte)0;
        arguments.CopyTo(header[8..]);

        using var des = DES.Create();
        des.Key = key;
        des.EncryptCbc(header, key, packet[..HeaderSize], PaddingMode.PKCS7);
        packet[510] = 0xa1;
        packet[511] = 0x1a;
    }

    /// <summary>释放 WinUSB 会话，并清除会话持有的命令密钥副本。</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            transport.Dispose();
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
