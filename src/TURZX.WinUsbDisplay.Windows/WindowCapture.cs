using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TURZX.WinUsbDisplay.Windows;

/// <summary>表示可用 Windows 窗口及其捕捉入口。</summary>
/// <param name="Handle">用于捕捉的 Windows 窗口句柄。</param>
/// <param name="Title">窗口在列表中显示的标题。</param>
internal sealed record WindowCapture(nint Handle, string Title)
{
    /// <summary>已完成截图可被同尺寸捕捉请求复用的时长。</summary>
    private static readonly TimeSpan SharedSnapshotLifetime = TimeSpan.FromSeconds(1d / 30);
    /// <summary>保护共享截图任务和近期快照字典。</summary>
    private static readonly object PendingSnapshotsLock = new();
    /// <summary>按窗口句柄和尺寸合并正在进行的同类捕捉。</summary>
    private static readonly Dictionary<(nint Handle, int Width, int Height), TaskCompletionSource<BitmapSource?>> PendingSnapshots = [];
    /// <summary>用弱引用保存近期完成的截图。</summary>
    private static readonly Dictionary<(nint Handle, int Width, int Height), (long Completed, WeakReference<BitmapSource> Image)> RecentSnapshots = [];

    /// <summary>返回供窗口选择器显示的窗口标题。</summary>
    public override string ToString() => Title;

    /// <summary>枚举可以捕捉的顶层窗口，排除 Studio 自身。</summary>
    internal static IReadOnlyList<WindowCapture> List(nint ownWindow)
    {
        var windows = new List<WindowCapture>();
        EnumWindows((handle, _) =>
        {
            if (handle == ownWindow || !IsWindowVisible(handle) || IsIconic(handle))
                return true;
            int length = GetWindowTextLengthW(handle);
            if (length > 0)
            {
                var name = new StringBuilder(length + 1);
                if (GetWindowTextW(handle, name, name.Capacity) > 0)
                    windows.Add(new WindowCapture(handle, name.ToString()));
            }
            return true;
        }, 0);
        return windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>同步捕捉目标窗口并编码为 PNG。</summary>
    internal byte[] Capture(DisplayModel target)
    {
        return EncodePng(CaptureBitmap(target));
    }

    /// <summary>将独立位图编码为 PNG 字节。</summary>
    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>使用可复用画布异步截图并编码为 PNG。</summary>
    internal async Task<byte[]> CaptureAsync(DisplayModel target, CaptureSurface surface, CancellationToken cancellation) =>
        EncodePng(await CaptureNextBitmapAsync(target, surface, cancellation).ConfigureAwait(false));

    /// <summary>等待窗口恢复并重试暂时缺失的帧。</summary>
    internal async Task<BitmapSource> CaptureNextBitmapAsync(DisplayModel target, CaptureSurface surface,
        CancellationToken cancellation, bool nativeSize = false, BitmapSource? previousFrame = null)
    {
        int missedFrames = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!IsIconic(Handle))
            {
                BitmapSource? bitmap = await surface.RunAsync(token =>
                {
                    var frame = CaptureBitmapCore(target, surface, nativeSize, token, skipMinimized: true);
                    frame?.Freeze();
                    return frame;
                }, cancellation).ConfigureAwait(false);
                if (bitmap is not null)
                    return bitmap;
            }

            if (IsIconic(Handle))
            {
                missedFrames = 0;
                if (previousFrame is not null)
                    return previousFrame;
            }
            else if (++missedFrames >= 6)
                throw FrameUnavailable();

            await Task.Delay(200, cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>使用可复用画布异步捕捉位图。</summary>
    internal Task<BitmapSource> CaptureBitmapAsync(DisplayModel target, CaptureSurface surface,
        CancellationToken cancellation, bool nativeSize = false) => surface.RunAsync(token =>
    {
        BitmapSource bitmap = CaptureBitmap(target, surface, nativeSize, token);
        bitmap.Freeze();
        return bitmap;
    }, cancellation);

    /// <summary>使用临时画布捕捉适应设备尺寸的位图。</summary>
    internal BitmapSource CaptureBitmap(DisplayModel target)
    {
        using var surface = new CaptureSurface();
        return CaptureBitmap(target, surface);
    }

    /// <summary>捕捉窗口位图，可选择保留源窗口像素尺寸。</summary>
    internal BitmapSource CaptureBitmap(DisplayModel target, CaptureSurface surface, bool nativeSize = false,
        CancellationToken cancellation = default) =>
        CaptureBitmapCore(target, surface, nativeSize, cancellation, skipMinimized: false) ?? throw FrameUnavailable();

    private static IOException FrameUnavailable() =>
        LocalizableError.Mark(new IOException("This window did not provide a frame."), "ErrorWindowFrameUnavailable");

    private BitmapSource? CaptureBitmapCore(DisplayModel target, CaptureSurface surface, bool nativeSize,
        CancellationToken cancellation, bool skipMinimized)
    {
        cancellation.ThrowIfCancellationRequested();
        bool minimized = IsIconic(Handle);
        if (skipMinimized && minimized)
            return null;
        if (minimized || !IsWindowVisible(Handle) || IsHungAppWindow(Handle) || !GetWindowRect(Handle, out Rect bounds))
            throw LocalizableError.Mark(new IOException("The window is closed, minimized or unresponsive. Restore it and try again."), "ErrorCaptureWindowUnavailable");

        int width = bounds.Right - bounds.Left;
        int height = bounds.Bottom - bounds.Top;
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw LocalizableError.Mark(new IOException("The window is outside the supported capture size."), "ErrorCaptureWindowTooLarge");

        BitmapSource? bitmap;
        try
        {
            bitmap = ReadSharedSnapshot(surface, width, height, cancellation);
        }
        catch (IOException) when (skipMinimized && IsIconic(Handle))
        {
            return null;
        }
        if (bitmap is null || (skipMinimized && IsIconic(Handle)))
            return null;
        if (nativeSize)
            return bitmap;
        // 保留窗口宽高比，图层布局由预览和送屏各应用一次。
        int maxEdge = Math.Max(target.Width, target.Height);
        double scale = Math.Min(1d, (double)maxEdge / Math.Max(width, height));
        if (scale >= 1)
            return bitmap;

        double scaledWidth = Math.Max(1, Math.Round(width * scale));
        double scaledHeight = Math.Max(1, Math.Round(height * scale));
        return new TransformedBitmap(bitmap, new ScaleTransform(scaledWidth / width, scaledHeight / height));
    }

    /// <summary>复用同窗口同尺寸的待完成截图或最近快照。</summary>
    private BitmapSource? ReadSharedSnapshot(CaptureSurface surface, int width, int height, CancellationToken cancellation)
    {
        var key = (Handle, width, height);
        TaskCompletionSource<BitmapSource?> pending;
        bool capture;
        lock (PendingSnapshotsLock)
        {
            // 连续捕捉的节拍可能错开。短时间内允许另一条播放链路读取刚完成的截图。
            if (surface.LastSnapshot is not null && RecentSnapshots.TryGetValue(key, out var recent) &&
                Stopwatch.GetElapsedTime(recent.Completed) < SharedSnapshotLifetime &&
                recent.Image.TryGetTarget(out var image) && !ReferenceEquals(surface.LastSnapshot, image))
            {
                surface.LastSnapshot = image;
                return image;
            }

            // 首个请求登记并实际抓取画面，后续同尺寸请求在锁外等同一个结果。
            capture = !PendingSnapshots.TryGetValue(key, out pending!);
            if (capture)
            {
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                PendingSnapshots.Add(key, pending);
            }
        }
        if (!capture)
            return RememberSnapshot(pending.Task.WaitAsync(cancellation).GetAwaiter().GetResult());

        try
        {
            // 冻结独立位图，供捕捉同一窗口的设备各自缩放和编码。
            BitmapSource? snapshot = surface.Read(Handle, width, height);
            if (snapshot is not null)
            {
                snapshot.Freeze();
                lock (PendingSnapshotsLock)
                {
                    if (RecentSnapshots.Count >= 16 && !RecentSnapshots.ContainsKey(key))
                        RecentSnapshots.Clear();
                    RecentSnapshots[key] = (Stopwatch.GetTimestamp(), new(snapshot));
                }
            }
            pending.SetResult(snapshot);
        }
        catch (Exception error)
        {
            pending.SetException(error);
        }
        finally
        {
            lock (PendingSnapshotsLock)
                PendingSnapshots.Remove(key);
        }

        return RememberSnapshot(pending.Task.GetAwaiter().GetResult());

        BitmapSource? RememberSnapshot(BitmapSource? snapshot)
        {
            if (snapshot is not null)
                surface.LastSnapshot = snapshot;
            return snapshot;
        }
    }

    /// <summary>在一次连续捕捉中复用 GDI 画布。调用方须串行读取并释放画布。</summary>
    internal sealed class CaptureSurface : IDisposable
    {
        /// <summary>复用的设备上下文、DIB、原选中对象和像素地址。</summary>
        private nint dc, dib, previous, pixels;
        /// <summary>当前 DIB 的像素宽度和高度。</summary>
        private int width, height;
        /// <summary>正在使用 GDI 资源的捕捉任务。</summary>
        private Task? inFlight;
        /// <summary>指示画布是否已经进入释放流程。</summary>
        private bool disposed;
        /// <summary>为本次捕捉来源保留的最近一张位图。</summary>
        internal BitmapSource? LastSnapshot { get; set; }

        /// <summary>跟踪异步捕捉并使调用方可取消等待。</summary>
        internal async Task<TResult> RunAsync<TResult>(Func<CancellationToken, TResult> capture, CancellationToken cancellation)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (inFlight is { IsCompleted: false })
                throw new InvalidOperationException("The capture surface is still in use.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            CancellationToken token = timeout.Token;
            var work = Task.Run(() => capture(token), token);
            inFlight = work;
            try
            {
                return await work.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw LocalizableError.Mark(new TimeoutException("Window capture timed out. Restore the target window and try again."), "ErrorWindowCaptureTimedOut");
            }
        }

        /// <summary>将窗口绘制到 DIB，再复制为独立位图。</summary>
        internal BitmapSource? Read(nint window, int width, int height)
        {
            if (dc == 0 || this.width != width || this.height != height)
                Resize(window, width, height);

            // 清除上一帧，避免只绘制部分区域的窗口留下残影。
            bool drawn = PatBlt(dc, 0, 0, width, height, 0x00000042) && PrintWindow(window, dc, 2);
            if (!GdiFlush() || !drawn)
                return null;

            // 等待当前线程的 GDI 绘制完成，再从 DIB 复制独立位图。
            int stride = checked(width * 4);
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
                pixels, checked(stride * height), stride);
        }

        /// <summary>按所需像素尺寸重新分配 GDI 画布。</summary>
        private void Resize(nint window, int width, int height)
        {
            ReleaseResources();
            // 负高度创建从顶行开始存放的 DIB，使像素行顺序与 WPF 位图一致。
            var info = new BitmapInfo
            {
                Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                Width = width, Height = -height, Planes = 1, Bits = 32
            };
            nint windowDc = GetDC(window);
            if (windowDc != 0)
            {
                dc = CreateCompatibleDC(windowDc);
                ReleaseDC(window, windowDc);
            }
            if (dc == 0)
                throw LocalizableError.Mark(new IOException("Could not create the window capture surface."), "ErrorCreateCaptureSurface");
            dib = CreateDIBSection(dc, ref info, 0, out pixels, 0, 0);
            if (dib != 0)
                previous = SelectObject(dc, dib);
            if (previous == 0 || previous == -1)
            {
                ReleaseResources();
                throw LocalizableError.Mark(new IOException("Could not allocate the window capture surface."), "ErrorAllocateCaptureSurface");
            }
            this.width = width;
            this.height = height;
        }

        /// <summary>释放 GDI 画布，或等待仍在执行的捕捉结束后释放。</summary>
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            if (inFlight is { } work)
                _ = ReleaseAfterCaptureAsync(work);
            else
                ReleaseResources();
        }

        /// <summary>等待捕捉结束后释放它仍在使用的资源。</summary>
        private async Task ReleaseAfterCaptureAsync(Task work)
        {
            // 调用方停止等待后，PrintWindow 可能仍在写入 DIB。
            await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ReleaseResources();
        }

        /// <summary>还原选中对象并删除画布持有的 GDI 资源。</summary>
        private void ReleaseResources()
        {
            if (previous != 0 && previous != -1)
                SelectObject(dc, previous);
            if (dib != 0)
                DeleteObject(dib);
            if (dc != 0)
                DeleteDC(dc);
            dc = dib = previous = pixels = 0;
            LastSnapshot = null;
        }
    }

    /// <summary>与 Win32 RECT 对应的窗口边界结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        /// <summary>矩形的左、上、右、下边界坐标。</summary>
        internal int Left, Top, Right, Bottom;
    }

    /// <summary>描述 DIB 像素格式和尺寸的 Win32 结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        /// <summary>结构体头的大小，单位为字节。</summary>
        internal uint Size;
        /// <summary>位图的宽度和高度，单位为像素。</summary>
        internal int Width, Height;
        /// <summary>颜色平面数和每像素位数。</summary>
        internal ushort Planes, Bits;
        /// <summary>压缩格式及位图数据的字节数。</summary>
        internal uint Compression, ImageSize;
        /// <summary>横向和纵向的每米像素数。</summary>
        internal int XPelsPerMeter, YPelsPerMeter;
        /// <summary>调色板中的颜色总数和重要颜色数。</summary>
        internal uint ColorsUsed, ColorsImportant;
    }

    /// <summary>Windows 顶层窗口枚举的回调签名。</summary>
    private delegate bool WindowVisitor(nint handle, nint parameter);
    /// <summary>枚举所有顶层窗口，并逐个调用访问回调。</summary>
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor callback, nint parameter);
    /// <summary>查询窗口是否设置可见标志。</summary>
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    /// <summary>查询窗口是否已最小化。</summary>
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    /// <summary>判断窗口是否未响应。</summary>
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    /// <summary>读取窗口在屏幕上的边界矩形。</summary>
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect bounds);
    /// <summary>获取窗口标题长度以分配文本缓冲区。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextLengthW(nint window);
    /// <summary>将窗口标题读入调用方提供的缓冲区。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int maxCount);
    /// <summary>请求目标窗口把内容绘制到指定设备上下文。</summary>
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    /// <summary>获取窗口的设备上下文。</summary>
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    /// <summary>释放 GetDC 取得的设备上下文。</summary>
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    /// <summary>创建与原设备上下文兼容的内存设备上下文。</summary>
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    /// <summary>创建 DIB 并返回可直接访问的像素缓冲区。</summary>
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage,
        out nint pixels, nint section, uint offset);
    /// <summary>将 GDI 对象选入设备上下文，并返回此前的对象。</summary>
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint handle);
    /// <summary>用指定光栅操作填充设备上下文中的区域。</summary>
    [DllImport("gdi32.dll")] private static extern bool PatBlt(nint dc, int x, int y, int width, int height, uint operation);
    /// <summary>完成当前线程尚未提交的 GDI 绘制。</summary>
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    /// <summary>释放 DIB 等 GDI 对象。</summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    /// <summary>释放创建的内存设备上下文。</summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
