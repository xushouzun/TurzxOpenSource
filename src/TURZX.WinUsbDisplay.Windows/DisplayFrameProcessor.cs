using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TURZX.WinUsbDisplay.Windows;

/// <summary>将源图片和视频帧裁剪、合成并编码为设备可接收的画面。</summary>
internal static class DisplayFrameProcessor
{
    /// <summary>设备单帧编码结果允许的最大字节数。</summary>
    internal const int MaxEncodedFrameBytes = DisplaySession.MaxEncodedFrameBytes;

    /// <summary>按目标画布处理图片，并按需保留透明度。</summary>
    internal static byte[] Prepare(byte[] image, DisplayModel model, bool preserveTransparency,
        MediaLayout? layout = null, byte rotation = 0) =>
        PrepareLayers(image, layout ?? MediaLayout.Default, null, MediaLayout.Default, model, preserveTransparency, rotation);

    /// <summary>将编码的前景和背景图片解码并按各自布局合成。</summary>
    internal static byte[] PrepareLayers(byte[]? foreground, MediaLayout foregroundLayout, byte[]? background,
        MediaLayout backgroundLayout, DisplayModel model, bool preserveTransparency = false, byte rotation = 0) =>
        PrepareBitmapLayers(foreground is null ? null : DecodeFrame(foreground), foregroundLayout,
            background is null ? null : DecodeFrame(background), backgroundLayout, model, preserveTransparency, rotation);

    /// <summary>合成并编码用于播放的图片帧。</summary>
    internal static byte[] PreparePlaybackImage(byte[]? foreground, MediaLayout foregroundLayout, byte[]? background,
        MediaLayout backgroundLayout, DisplayModel model, byte rotation = 0)
    {
        if (model.ProductId != 0x0021)
            return PrepareLayers(foreground, foregroundLayout, background, backgroundLayout, model, rotation: rotation);

        return PreparePlaybackJpeg(foreground, foregroundLayout, background, backgroundLayout, model, rotation)
            ?? PrepareLayers(foreground, foregroundLayout, background, backgroundLayout, model, rotation: rotation);
    }

    /// <summary>合成轮播预览，并按设备帧大小限制编码同一画面。</summary>
    internal static (byte[] Frame, BitmapSource Preview) PrepareSlideshowFrame(byte[] image, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, bool overlay, byte rotation,
        bool preferJpeg = false)
    {
        BitmapSource foreground = DecodeFrame(image);
        var canvas = model.ForRotation(rotation);
        BitmapSource rendered = Render(foreground, background, canvas.Width, canvas.Height,
            overlay, foregroundLayout, backgroundLayout);
        BitmapSource preview = RotateToNative(rendered, rotation);
        preview.Freeze();

        byte[] frame = (!overlay && (preferJpeg || model.ProductId == 0x0021) ? EncodePlaybackJpeg(preview, 0) : null)
            ?? PrepareBitmapLayers(foreground, foregroundLayout, background, backgroundLayout,
                model, overlay, rotation, nativeFrame: preview);
        return (frame, preview);
    }

    /// <summary>合成并尝试编码可直接送屏的 JPEG 帧。</summary>
    internal static byte[]? PreparePlaybackJpeg(byte[]? foreground, MediaLayout foregroundLayout, byte[]? background,
        MediaLayout backgroundLayout, DisplayModel model, byte rotation = 0)
    {
        var canvas = model.ForRotation(rotation);
        BitmapSource rendered = Render(foreground is null ? null : DecodeFrame(foreground),
            background is null ? null : DecodeFrame(background), canvas.Width, canvas.Height,
            false, foregroundLayout, backgroundLayout);
        return EncodePlaybackJpeg(rendered, rotation);
    }

    /// <summary>将 JPEG 输入处理为符合目标设备尺寸的送屏帧。</summary>
    internal static byte[] PrepareDirectJpegFrame(byte[] frame, DisplayModel model)
    {
        if (frame.Length <= MaxEncodedFrameBytes && frame.AsSpan().StartsWith<byte>([0xff, 0xd8, 0xff]))
            return frame;

        return PreparePlaybackJpeg(frame, MediaLayout.Default, null, MediaLayout.Default, model)
            ?? throw new InvalidDataException("The live frame exceeds the JPEG size limit.");
    }

    /// <summary>将已解码前景与背景按指定布局合成。</summary>
    internal static byte[] PrepareBitmapLayers(BitmapSource? foreground, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, bool preserveTransparency, byte rotation,
        bool preferFastPng = false, BitmapSource? nativeFrame = null)
    {
        model = model.ForRotation(rotation);
        if (foreground is null && background is null)
            throw new ArgumentException("An image layer is required.");

        foreach (double scale in new[] { 1d, .75, .5, .25 })
        {
            int width = Math.Max(1, (int)(model.Width * scale));
            int height = Math.Max(1, (int)(model.Height * scale));
            // 将设备像素偏移换算为当前渲染画布的像素偏移。
            MediaLayout ScaleOffsets(MediaLayout layout) => layout with
            {
                OffsetX = (int)Math.Round(layout.OffsetX * (double)width / model.Width),
                OffsetY = (int)Math.Round(layout.OffsetY * (double)height / model.Height)
            };
            BitmapSource frame = scale == 1d && nativeFrame is not null ? nativeFrame : RenderAtScale();

            BitmapSource RenderAtScale()
            {
                BitmapSource sampled = Render(foreground, background, width, height, preserveTransparency,
                    ScaleOffsets(foregroundLayout), ScaleOffsets(backgroundLayout));
                BitmapSource scaled = scale switch
                {
                    1d => sampled,
                    .25 => UpscaleNearestNeighbor(sampled, model.Width, model.Height),
                    _ => new TransformedBitmap(sampled,
                        new ScaleTransform((double)model.Width / width, (double)model.Height / height))
                };
                return RotateToNative(scaled, rotation);
            }

            if (preferFastPng && scale == 1d)
            {
                byte[] fast = LivePngEncoder.Encode(frame, preserveTransparency);
                if (fast.Length <= MaxEncodedFrameBytes)
                    return fast;
            }
            var data = EncodeRgbaPng(frame);
            if (data.Length <= MaxEncodedFrameBytes)
                return data;
        }

        throw new InvalidDataException("The image exceeds the device frame size limit.");
    }

    /// <summary>用最近邻采样放大位图。</summary>
    private static BitmapSource UpscaleNearestNeighbor(BitmapSource source, int width, int height)
    {
        // 保持设备原生画面尺寸，同时避免插值增大 PNG 文件。
        int sourceStride = source.PixelWidth * 4;
        int stride = width * 4;
        var samples = new int[source.PixelWidth * source.PixelHeight];
        source.CopyPixels(samples, sourceStride, 0);
        var pixels = new int[width * height];
        int lastSourceRow = -1;
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * source.PixelHeight / height;
            int row = y * width;
            if (sourceRow == lastSourceRow)
                Array.Copy(pixels, row - width, pixels, row, width);
            else
            {
                int start = sourceRow * source.PixelWidth;
                for (int x = 0; x < width; x++)
                    pixels[row + x] = samples[start + x * source.PixelWidth / width];
                lastSourceRow = sourceRow;
            }
        }
        return BitmapSource.Create(width, height, 96, 96, source.Format, null, pixels, stride);
    }

    /// <summary>准备网页截图生成的实时送屏帧。</summary>
    internal static byte[] PrepareLiveHtmlFrame(byte[] screenshot, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, bool preserveTransparency,
        byte rotation, Rect? crop, BitmapScalingMode scalingMode = BitmapScalingMode.HighQuality)
    {
        BitmapSource bitmap = DecodeFrame(screenshot);
        if (crop is null && background is null && rotation == 0 && preserveTransparency
            && foregroundLayout is { ZoomPercent: 100, OffsetX: 0, OffsetY: 0 }
            && bitmap.PixelWidth == model.Width && bitmap.PixelHeight == model.Height
            && screenshot.Length <= MaxEncodedFrameBytes && LivePngEncoder.IsRgba(screenshot))
            return screenshot;

        return PrepareLiveBitmapFrame(bitmap, foregroundLayout, background, backgroundLayout, model,
            preserveTransparency, rotation, crop, scalingMode);
    }

    /// <summary>将已捕捉的位图转换为实时送屏帧。</summary>
    internal static byte[] PrepareLiveBitmapFrame(BitmapSource bitmap, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, bool preserveTransparency,
        byte rotation, Rect? crop, BitmapScalingMode scalingMode = BitmapScalingMode.HighQuality,
        bool preferJpeg = false)
    {
        var canvas = model.ForRotation(rotation);
        bool matchesCanvas = crop is null && background is null
            && foregroundLayout is { ZoomPercent: 100, OffsetX: 0, OffsetY: 0 }
            && bitmap.PixelWidth == canvas.Width && bitmap.PixelHeight == canvas.Height;
        if (preferJpeg && !preserveTransparency)
        {
            BitmapSource rendered = matchesCanvas
                ? new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0)
                : Render(CropCapture(bitmap, crop), background, canvas.Width, canvas.Height,
                    false, foregroundLayout, backgroundLayout, scalingMode);
            if (EncodePlaybackJpeg(rendered, rotation) is { } jpeg)
                return jpeg;
        }
        if (matchesCanvas)
        {
            BitmapSource native = rotation == 0 ? bitmap
                : new TransformedBitmap(bitmap, new RotateTransform((4 - rotation) * 90));
            byte[] fast = LivePngEncoder.Encode(native, preserveTransparency);
            if (fast.Length <= MaxEncodedFrameBytes)
                return fast;
        }

        return PrepareBitmapLayers(CropCapture(bitmap, crop), foregroundLayout, background, backgroundLayout,
            model, preserveTransparency, rotation, preferFastPng: model.ProductId == 0x0021);
    }

    /// <summary>渲染实时视频前景并生成编码输入帧。</summary>
    internal static byte[] PrepareForegroundVideoFrame(BitmapSource bitmap, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, byte rotation, Rect? crop,
        DisplayModel? outputCanvas = null)
        => PrepareForegroundJpeg(bitmap, foregroundLayout, background, backgroundLayout, model, rotation, crop, outputCanvas)
            ?? throw new InvalidDataException("The foreground frame exceeds the JPEG size limit.");

    /// <summary>尝试将实时前景编码为 JPEG。</summary>
    internal static byte[]? PrepareForegroundJpeg(BitmapSource bitmap, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, byte rotation, Rect? crop,
        DisplayModel? outputCanvas = null)
    {
        var rendered = RenderForegroundVideoFrame(bitmap, foregroundLayout, background, backgroundLayout, model, rotation, crop, outputCanvas);
        return EncodePlaybackJpeg(rendered, rotation);
    }

    /// <summary>将实时前景转换为原始视频编码所需的像素。</summary>
    internal static byte[] PrepareForegroundVideoPixels(BitmapSource bitmap, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, byte rotation, Rect? crop,
        DisplayModel? outputCanvas = null, byte[]? destination = null)
    {
        BitmapSource rendered = RenderForegroundVideoFrame(bitmap, foregroundLayout, background, backgroundLayout,
            model, rotation, crop, outputCanvas);
        if (rotation != 0)
            rendered = new TransformedBitmap(rendered, new RotateTransform((4 - rotation) * 90));
        int stride = checked(rendered.PixelWidth * 4);
        // CopyPixels 写满整个输出，跳过大数组的预清零。
        int length = checked(stride * rendered.PixelHeight);
        var pixels = destination?.Length == length ? destination : GC.AllocateUninitializedArray<byte>(length);
        rendered.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>按前景布局将画面合成到背景视频像素中。</summary>
    internal static byte[] CompositeVideoPixels(BitmapSource foreground, MediaLayout layout,
        byte[] background, DisplayModel model, DisplayModel output, Rect? crop,
        Action<TimeSpan, TimeSpan>? onTiming = null)
    {
        int stride = checked(output.Width * 4);
        int length = checked(stride * output.Height);
        long started = onTiming is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        try
        {
            RenderVideoOverlay(foreground, layout, model, output, crop, pixels);
            // 前景使用预乘 Alpha，混合时直接原地修改背景缓冲区，避免再分配一张整帧。
            long rendered = onTiming is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
            BlendVideoOverlay(pixels, background);
            onTiming?.Invoke(System.Diagnostics.Stopwatch.GetElapsedTime(started, rendered),
                System.Diagnostics.Stopwatch.GetElapsedTime(rendered));
            return background;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    /// <summary>将前景缩放到编码画布，写入预乘 Alpha 像素缓冲区。</summary>
    internal static void RenderVideoOverlay(BitmapSource foreground, MediaLayout layout, DisplayModel model,
        DisplayModel output, Rect? crop, byte[] pixels)
    {
        var layer = Render(CropCapture(foreground, crop), null, output.Width, output.Height, true,
            layout with
            {
                OffsetX = (int)(layout.OffsetX * ((double)output.Width / model.Width)),
                OffsetY = (int)(layout.OffsetY * ((double)output.Height / model.Height))
            }, MediaLayout.Default, BitmapScalingMode.LowQuality);
        layer.CopyPixels(pixels, checked(output.Width * 4), 0);
    }

    /// <summary>把预乘 Alpha 前景混合到当前背景帧，前景缓冲区可继续复用。</summary>
    internal static byte[] BlendVideoOverlay(byte[] pixels, byte[] background)
    {
        for (int i = 0; i < background.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            if (alpha == 0)
                continue;
            // 前景色已乘过 Alpha，只需按剩余透明度缩放背景色。
            int inverse = 255 - alpha;
            for (int channel = i; channel < i + 3; channel++)
                background[channel] = (byte)(pixels[channel] + (background[channel] * inverse + 127) / 255);
        }
        return background;
    }

    /// <summary>按布局构造供实时视频编码使用的前景位图。</summary>
    private static BitmapSource RenderForegroundVideoFrame(BitmapSource bitmap, MediaLayout foregroundLayout,
        BitmapSource? background, MediaLayout backgroundLayout, DisplayModel model, byte rotation, Rect? crop,
        DisplayModel? outputCanvas = null)
    {
        var canvas = model.ForRotation(rotation);
        var output = outputCanvas?.ForRotation(rotation);
        int width = output?.Width ?? canvas.Width / 2;
        int height = output?.Height ?? canvas.Height / 2;
        return Render(CropCapture(bitmap, crop), background, width, height,
            false, ScaleOffsets(foregroundLayout), ScaleOffsets(backgroundLayout), BitmapScalingMode.LowQuality);

        // 将设备像素偏移换算为当前渲染画布的像素偏移。
        MediaLayout ScaleOffsets(MediaLayout layout) => layout with
        {
            OffsetX = (int)(layout.OffsetX * (output is null ? 0.5 : (double)width / canvas.Width)),
            OffsetY = (int)(layout.OffsetY * (output is null ? 0.5 : (double)height / canvas.Height))
        };
    }

    // H.264 的 YUV 4:2:0 画布宽高都需要为偶数。
    /// <summary>计算网页视频编码使用的画布尺寸。</summary>
    internal static DisplayModel HtmlVideoCanvas(DisplayModel model) =>
        model with { Width = model.Width / 5 * 2, Height = model.Height / 5 * 2 };

    /// <summary>把原始视频像素编码为可显示的预览帧。</summary>
    internal static byte[] EncodeVideoPreview(byte[] pixels, DisplayModel canvas)
    {
        var bitmap = BitmapSource.Create(canvas.Width, canvas.Height, 96, 96,
            PixelFormats.Bgr32, null, pixels, checked(canvas.Width * 4));
        return EncodePlaybackJpeg(bitmap, 0)
            ?? throw new InvalidDataException("The preview frame exceeds the JPEG size limit.");
    }

    /// <summary>在设备帧大小限制内尝试编码 JPEG。</summary>
    private static byte[]? EncodePlaybackJpeg(BitmapSource bitmap, byte rotation)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(RotateToNative(bitmap, rotation)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.Length <= MaxEncodedFrameBytes ? output.ToArray() : null;
    }

    private static BitmapSource RotateToNative(BitmapSource bitmap, byte rotation) =>
        rotation == 0 ? bitmap : new TransformedBitmap(bitmap, new RotateTransform((4 - rotation) * 90));

    /// <summary>按归一化区域裁剪已解码的捕捉帧。</summary>
    internal static BitmapSource CropCapture(BitmapSource frame, Rect? region)
    {
        if (region is not { } bounds)
            return frame;

        int left = Math.Min(frame.PixelWidth - 1, (int)Math.Floor(bounds.Left * frame.PixelWidth));
        int top = Math.Min(frame.PixelHeight - 1, (int)Math.Floor(bounds.Top * frame.PixelHeight));
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right * frame.PixelWidth), left + 1, frame.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * frame.PixelHeight), top + 1, frame.PixelHeight);
        return new CroppedBitmap(frame, new Int32Rect(left, top, right - left, bottom - top));
    }

    /// <summary>解码完整的图片字节，并使位图独立于输入流。</summary>
    internal static BitmapSource DecodeFrame(byte[] image)
    {
        using var input = new MemoryStream(image, writable: false);
        return BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
    }

    /// <summary>将图层绘制到指定大小和方向的输出位图。</summary>
    private static BitmapSource Render(BitmapSource? foreground, BitmapSource? background, int width, int height,
        bool transparent, MediaLayout foregroundLayout, MediaLayout backgroundLayout,
        BitmapScalingMode scalingMode = BitmapScalingMode.HighQuality)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, scalingMode);
        using (var drawing = visual.RenderOpen())
        {
            if (!transparent)
                drawing.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            DrawLayer(drawing, background, backgroundLayout);
            DrawLayer(drawing, foreground, foregroundLayout);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;

        // 按缩放和偏移将有内容的图层绘制到合成画布。
        void DrawLayer(DrawingContext drawing, BitmapSource? image, MediaLayout layout)
        {
            if (image is null)
                return;
            double widthRatio = (double)width / image.PixelWidth;
            double heightRatio = (double)height / image.PixelHeight;
            double ratio = (layout.Fill ? Math.Max(widthRatio, heightRatio) : Math.Min(widthRatio, heightRatio))
                * layout.ZoomFactor;
            double imageWidth = image.PixelWidth * ratio;
            double imageHeight = image.PixelHeight * ratio;
            drawing.DrawImage(image, new Rect(
                (width - imageWidth) / 2 + layout.OffsetX,
                (height - imageHeight) / 2 + layout.OffsetY, imageWidth, imageHeight));
        }
    }

    /// <summary>创建与设备画布同尺寸的透明覆盖帧。</summary>
    internal static byte[] CreateBlankOverlay(DisplayModel model) =>
        EncodeRgbaPng(Render(null, null, model.Width, model.Height, true, MediaLayout.Default, MediaLayout.Default));

    /// <summary>将位图编码为保留透明通道的 PNG。</summary>
    private static byte[] EncodeRgbaPng(BitmapSource source)
    {
        // 部分 Win207 固件会应答 RGB PNG 和 JPEG 帧，但无法正确显示。
        var rgba = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rgba));
        using var output = new MemoryStream();
        encoder.Save(output);
        byte[] bytes = output.ToArray();
        if (bytes.Length < 26 || bytes[24] != 8 || bytes[25] != 6)
            throw new InvalidDataException("The Win207 display requires an 8-bit RGBA PNG.");
        return bytes;
    }
}
