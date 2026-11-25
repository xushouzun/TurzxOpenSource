using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>在设备逻辑画布中绘制前景、背景和捕捉选区。</summary>
public partial class PreviewCanvas : UserControl
{
    /// <summary>当前播放器打开的本地视频路径。</summary>
    private string? videoFile;
    /// <summary>指示视频播放结束后是否循环。</summary>
    private bool repeat;
    /// <summary>指示当前视频文件由画布负责删除。</summary>
    private bool deleteVideoFile;
    /// <summary>预览画布使用的前景布局。</summary>
    private MediaLayout foregroundLayout = MediaLayout.Default;
    /// <summary>预览画布使用的背景布局。</summary>
    private MediaLayout backgroundLayout = MediaLayout.Default;
    /// <summary>指示视频是否占用背景层。</summary>
    private bool videoIsBackground;
    /// <summary>指示前景帧是否已完成送屏布局处理。</summary>
    private bool framePrepared;
    /// <summary>指示前景帧是否与设备原生方向一致。</summary>
    private bool frameIsNative;
    /// <summary>指示视频是否已按送屏画布准备。</summary>
    private bool videoPrepared;
    /// <summary>已准备视频所对应的画布方向。</summary>
    private int preparedVideoRotation;
    /// <summary>预览画布所模拟的设备型号。</summary>
    private DisplayModel? model;
    /// <summary>屏幕当前的四分之一圈旋转数。</summary>
    private int quarterTurns;
    /// <summary>指示画布是否正在进行来源裁剪框选。</summary>
    private bool selectingRegion;
    /// <summary>指示两个视频控件中哪一个当前可见。</summary>
    private bool useSecondVideo;
    /// <summary>备用播放器正在打开的视频及其所有权。</summary>
    private (string Path, bool Loop, bool OwnsFile)? pendingVideo;

    /// <summary>返回当前用于显示视频的播放器。</summary>
    private MediaElement Video => useSecondVideo ? VideoB : VideoA;
    /// <summary>返回用于准备下一段视频的备用播放器。</summary>
    private MediaElement StandbyVideo => useSecondVideo ? VideoA : VideoB;

    /// <summary>初始化预览画布和媒体控件。</summary>
    public PreviewCanvas() => InitializeComponent();

    /// <summary>将指针位置转换到设备画布坐标系。</summary>
    internal Point GetScreenPosition(MouseEventArgs e) => e.GetPosition(NativeScreen);

    /// <summary>切换裁剪框选模式并同步视觉状态。</summary>
    internal void SetRegionSelection(bool active)
    {
        selectingRegion = active;
        ShowRegionOutline(null);
        ApplyLayout();
    }

    /// <summary>将指针位置转换为源画面中的归一化坐标。</summary>
    internal Point GetRegionPoint(MouseEventArgs e)
    {
        Point point = e.GetPosition(ForegroundFrame);
        return new Point(Math.Clamp(point.X / ForegroundFrame.Width, 0, 1),
            Math.Clamp(point.Y / ForegroundFrame.Height, 0, 1));
    }

    /// <summary>判断指针是否位于当前可框选画面内。</summary>
    internal bool IsOverRegionFrame(MouseEventArgs e)
    {
        Point point = e.GetPosition(ForegroundFrame);
        return new Rect(0, 0, ForegroundFrame.Width, ForegroundFrame.Height).Contains(point);
    }

    /// <summary>绘制或隐藏归一化的框选轮廓。</summary>
    internal void ShowRegionOutline(Rect? region)
    {
        RegionOutline.Visibility = region.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (region is not { } bounds)
            return;
        var origin = ForegroundFrame.TranslatePoint(new Point(0, 0), NativeScreen);
        Canvas.SetLeft(RegionOutline, origin.X + bounds.Left * ForegroundFrame.Width);
        Canvas.SetTop(RegionOutline, origin.Y + bounds.Top * ForegroundFrame.Height);
        RegionOutline.Width = bounds.Width * ForegroundFrame.Width;
        RegionOutline.Height = bounds.Height * ForegroundFrame.Height;
    }

    /// <summary>应用设备型号并按需保留当前前景。</summary>
    internal void SetModel(DisplayModel model, bool keepForeground = false)
    {
        this.model = model;
        ClearBackground();
        if (!keepForeground)
            ClearForeground();
        ApplyGeometry();
    }

    /// <summary>更新屏幕旋转方向，返回方向是否变化。</summary>
    internal bool SetRotation(int quarterTurns)
    {
        bool dimensionsChanged = ((this.quarterTurns ^ quarterTurns) & 1) != 0;
        this.quarterTurns = quarterTurns;
        ApplyGeometry();
        return dimensionsChanged;
    }

    /// <summary>根据型号和旋转设置画布几何尺寸。</summary>
    private void ApplyGeometry()
    {
        if (model is not { } screen)
            return;
        var canvas = screen.ForRotation(quarterTurns);
        NativeScreen.Width = canvas.Width;
        NativeScreen.Height = canvas.Height;
        ApplyLayout();
    }

    /// <summary>设置预览前景和背景的布局。</summary>
    internal void SetLayouts(MediaLayout foreground, MediaLayout background)
    {
        foregroundLayout = foreground;
        backgroundLayout = background;
        ApplyLayout();
    }

    /// <summary>将当前缩放和偏移应用于各图层。</summary>
    private void ApplyLayout()
    {
        if (model is not { } screen)
            return;

        var nativeModel = screen;
        screen = screen.ForRotation(quarterTurns);
        if (Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0)
        {
            var placement = videoPrepared ? MediaLayout.Default : videoIsBackground ? backgroundLayout : foregroundLayout;
            Place(Video, Video.NaturalVideoWidth, Video.NaturalVideoHeight, placement, videoPrepared ? preparedVideoRotation : null);
        }
        if (BackgroundFrame.Source is { } background)
            Place(BackgroundFrame, background.Width, background.Height, backgroundLayout);
        if (ForegroundFrame.Source is { } frame)
            Place(ForegroundFrame, frame.Width, frame.Height, framePrepared || selectingRegion ? MediaLayout.Default : foregroundLayout, frameIsNative ? quarterTurns : null);

        // 将一个图层按源尺寸、布局和原生旋转放置到画布。
        void Place(FrameworkElement element, double sourceWidth, double sourceHeight, MediaLayout placement, int? nativeRotation = null)
        {
            int frameRotation = nativeRotation is int rotation
                ? MainWindow.MediaFrameRotation(nativeModel, (byte)rotation) : 0;
            bool swap = (frameRotation & 1) != 0;
            double scaleX = screen.Width / (swap ? sourceHeight : sourceWidth);
            double scaleY = screen.Height / (swap ? sourceWidth : sourceHeight);
            element.RenderTransformOrigin = new Point(.5, .5);
            element.RenderTransform = frameRotation != 0
                ? new RotateTransform(frameRotation * 90) : Transform.Identity;
            double scale = placement.Fill ? Math.Max(scaleX, scaleY) : Math.Min(scaleX, scaleY);
            scale *= placement.ZoomFactor;
            element.Width = sourceWidth * scale;
            element.Height = sourceHeight * scale;
            Canvas.SetLeft(element, (screen.Width - element.Width) / 2 + placement.OffsetX);
            Canvas.SetTop(element, (screen.Height - element.Height) / 2 + placement.OffsetY);
        }
    }

    /// <summary>显示已编码的前景帧，可选择保留背景层。</summary>
    /// <remarks>已按设备布局处理的帧只需应用预览旋转。</remarks>
    internal void ShowFrame(byte[] frame, bool overlay, bool prepared = false, Rect? crop = null)
    {
        try
        {
            using var input = new MemoryStream(frame);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input;
            bitmap.EndInit();
            ShowFrame(bitmap, overlay, prepared, crop);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
        }
    }

    /// <summary>解码前景帧并应用设备布局或裁剪。</summary>
    internal void ShowFrame(BitmapSource frame, bool overlay, bool prepared, Rect? crop = null)
    {
        if (!overlay)
        {
            StopVideo();
            BackgroundFrame.Source = null;
        }
        BitmapSource image = DisplayFrameProcessor.CropCapture(frame, crop);
        bool needsLayout = framePrepared != prepared || frameIsNative != prepared
            || ForegroundFrame.Source is not { } previous || previous.Width != image.Width || previous.Height != image.Height;
        framePrepared = frameIsNative = prepared;
        ForegroundFrame.Source = image;
        if (needsLayout)
            ApplyLayout();
    }

    /// <summary>解码并显示背景图片。</summary>
    internal void ShowBackgroundImage(byte[] bytes)
    {
        ClearBackground();
        using var input = new MemoryStream(bytes, writable: false);
        BackgroundFrame.Source = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0];
        ApplyLayout();
    }

    /// <summary>显示 WPF 图片源作为预览前景。</summary>
    internal void ShowImage(ImageSource image)
    {
        framePrepared = frameIsNative = false;
        ForegroundFrame.Source = image;
        ApplyLayout();
    }

    /// <summary>恢复画布的空白状态并释放视频资源。</summary>
    internal void Reset()
    {
        StopVideo();
        ForegroundFrame.Source = BackgroundFrame.Source = null;
    }

    /// <summary>移除背景图片和背景视频。</summary>
    internal void ClearBackground()
    {
        StopVideo();
        BackgroundFrame.Source = null;
    }

    /// <summary>在指定图层播放视频，并记录关闭时是否应删除视频文件。</summary>
    internal void ShowVideo(string path, bool loop, bool ownsFile = true, bool prepared = false, bool background = false)
    {
        if (prepared && background && videoIsBackground && HasOpenedVideo)
        {
            if (videoFile == path)
            {
                // 用户切回当前画面时撤销之前的切换请求。
                CancelPendingVideo();
                return;
            }
            if (pendingVideo?.Path == path)
                return;

            // 保留当前背景，等准备好的视频打开后再切换播放器。
            CancelPendingVideo();
            pendingVideo = (path, loop, ownsFile);
            StandbyVideo.Opacity = 0;
            StandbyVideo.Source = new Uri(path);
            StandbyVideo.Visibility = Visibility.Visible;
            StandbyVideo.Play();
            return;
        }

        if (background)
            ClearBackground();
        else
            Reset();
        videoIsBackground = background;
        videoPrepared = prepared;
        preparedVideoRotation = quarterTurns;
        videoFile = path;
        deleteVideoFile = ownsFile;
        repeat = loop;
        Video.Source = new Uri(path);
        Video.Visibility = Visibility.Visible;
        Video.Play();
    }

    /// <summary>指示画布是否已有视频或静态背景图。</summary>
    internal bool HasBackground => videoFile is not null || BackgroundFrame.Source is not null;
    /// <summary>指示播放器是否已打开当前视频并取得尺寸。</summary>
    internal bool HasOpenedVideo => videoFile is not null && Video.NaturalVideoWidth > 0;
    /// <summary>指示当前视频是否已经按送屏布局准备。</summary>
    internal bool HasPreparedVideo => videoFile is not null && videoPrepared;

    /// <summary>清除显示在画布上的前景帧。</summary>
    internal void ClearForeground() => ForegroundFrame.Source = null;

    /// <summary>停止当前和待切换的视频，并释放所属文件。</summary>
    internal void StopVideo()
    {
        CancelPendingVideo();
        if (videoFile is null)
            return;
        CloseVideo(Video, videoFile, deleteVideoFile);
        videoFile = null;
    }

    /// <summary>取消在备用播放器中尚未打开的视频。</summary>
    private void CancelPendingVideo()
    {
        if (pendingVideo is not { } pending)
            return;
        pendingVideo = null;
        CloseVideo(StandbyVideo, pending.Path, pending.OwnsFile);
    }

    /// <summary>关闭播放器并按文件所有权清理路径。</summary>
    private static void CloseVideo(MediaElement player, string path, bool ownsFile)
    {
        player.Close();
        player.Source = null;
        player.Visibility = Visibility.Collapsed;
        player.Opacity = 1;
        if (ownsFile)
            TryDelete(path);
    }

    /// <summary>尽力删除预览拥有的临时视频。</summary>
    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>视频打开后更新播放器、画布布局和播放状态。</summary>
    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, Video))
        {
            ApplyLayout();
            return;
        }
        if (!ReferenceEquals(sender, StandbyVideo) || pendingVideo is not { } pending || videoFile is null)
            return;

        // 备用播放器打开成功后才互换角色，再关闭旧播放器并按所有权处理旧文件。
        string previousFile = videoFile;
        bool ownedPreviousFile = deleteVideoFile;
        pendingVideo = null;
        useSecondVideo = !useSecondVideo;
        videoFile = pending.Path;
        repeat = pending.Loop;
        deleteVideoFile = pending.OwnsFile;
        videoPrepared = true;
        preparedVideoRotation = quarterTurns;
        ApplyLayout();
        Video.Opacity = 1;
        CloseVideo(StandbyVideo, previousFile, ownedPreviousFile);
    }

    /// <summary>处理视频加载失败并清理对应资源。</summary>
    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, StandbyVideo))
            CancelPendingVideo();
        else if (ReferenceEquals(sender, Video))
            StopVideo();
    }

    /// <summary>在需要循环时回到视频开头继续播放。</summary>
    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, Video) && repeat)
        {
            Video.Position = TimeSpan.Zero;
            Video.Play();
        }
    }

}
