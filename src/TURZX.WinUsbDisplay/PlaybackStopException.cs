namespace TURZX.WinUsbDisplay;

/// <summary>主机已结束视频传输，但设备停播命令失败。</summary>
public sealed class PlaybackStopException : IOException
{
    internal PlaybackStopException(Exception stopFailure)
        : base("Video transmission ended, but the display did not confirm that playback stopped.", stopFailure)
    {
    }
}
