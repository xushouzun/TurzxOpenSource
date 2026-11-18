using System.Windows;
using System.Windows.Controls;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 更新设备列表及工作区中的播放指示。
public partial class MainWindow
{
    /// <summary>根据播放输出、续播队列和错误更新各设备标签。</summary>
    private void UpdatePlaybackIndicators()
    {
        if (PlaybackIndicator is null)
            return;

        foreach (var choice in WorkspaceDevice.Items.OfType<DeviceChoice>())
        {
            string path = choice.Device.Path;
            choice.SetPlaybackLabel(T(pendingResumes.ContainsKey(path) ? "WaitingToReconnect"
                : devicePlaybacks.TryGetValue(path, out var active) ? active.HasOutput ? "Playing" : "Preparing"
                : playbackErrors.ContainsKey(path) ? "PlaybackFailed" : "Idle"));
        }

        string? message = null;
        string? details = null;
        if (HasPendingResume)
            message = T("WaitingToReconnect");
        else if (CurrentPlayback is { SentFps: double fps } playback)
            message = playback.IsImageSlideshow
                ? T("MeasuredSlideInterval", 1 / fps)
                : T("MeasuredOutputRate", fps);
        else if (CurrentPlayback is { HasOutput: true, EncoderInputFps: double inputFps })
            message = T("EncoderInputRate", inputFps);
        else if (SelectedDevice is { } device && playbackErrors.TryGetValue(device.Path, out var error))
        {
            message = T("PlaybackFailureDetails", DescribeUiError(error));
            details = DescribeUiError(error, includeFullDetails: true);
        }

        PlaybackIndicator.Text = message ?? "";
        PlaybackIndicator.ToolTip = details is null ? message : new TextBlock
        {
            Text = details,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap
        };
        PlaybackIndicator.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
