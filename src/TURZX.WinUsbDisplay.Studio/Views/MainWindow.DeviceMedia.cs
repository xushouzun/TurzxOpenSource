using System.IO;
using System.Windows;
using System.Windows.Controls;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 管理显示设备存储中的媒体浏览、删除和播放。
public partial class MainWindow
{
    /// <summary>获取当前所选设备及存储卷对应的目录。</summary>
    private string? GetDeviceMediaDirectory() => SelectedDevice is not null
        && DeviceMediaFolder.SelectedItem is ComboBoxItem { Tag: string directory } ? directory : null;

    /// <summary>切换设备时重置文件浏览目录和列表。</summary>
    private void ResetDeviceMediaDirectory(DisplayDevice? device)
    {
        DeviceMediaFolder.SelectedIndex = device?.Model.ProductId == 0x0046 ? 2 : 0;
        ClearDeviceMediaList();
    }

    /// <summary>清除设备文件列表及当前选择。</summary>
    private void ClearDeviceMediaList()
    {
        DeviceMediaFiles.ItemsSource = null;
        ShowDeviceMediaMessage(null);
    }

    /// <summary>在设备媒体面板中显示操作状态。</summary>
    private void ShowDeviceMediaMessage(string? message)
    {
        DeviceMediaMessage.Text = message ?? "";
        DeviceMediaMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>将所选目录和文件名组合成设备端完整路径。</summary>
    private string? SelectedDeviceFilePath() =>
        GetDeviceMediaDirectory() is { } directory && DeviceMediaFiles.SelectedItem is string name
        && name.Length > 0 && name is not ("." or "..")
        && name.Length + directory.Length <= 484 && name.All(c => c is >= ' ' and <= '~' and not ('/' or '\\'))
            ? directory + name : null;

    /// <summary>检查当前选中的文件是否支持设备端播放。</summary>
    private bool CanPlayDeviceMedia() => SelectedDeviceFilePath() is { } path
        && Path.GetExtension(path).ToLowerInvariant() is ".h264" or ".264" or ".mp4" or ".jpg" or ".jpeg" or ".png" or ".bmp";

    /// <summary>根据目录和文件选择更新操作按钮。</summary>
    private void UpdateDeviceMediaControls()
    {
        if (DeviceMediaFiles is null)
            return;

        bool canControl = !busy && SelectedDevice is not null;
        DeviceMediaFolder.IsEnabled = DeviceMediaFiles.IsEnabled = StopDeviceMedia.IsEnabled = canControl;
        string? directory = GetDeviceMediaDirectory();
        DeviceMediaPath.Text = directory ?? "";
        DeviceMediaPath.Visibility = directory is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshDeviceMedia.IsEnabled = canControl && directory is not null;
        PlayDeviceMedia.IsEnabled = canControl && CanPlayDeviceMedia();
        DeleteDeviceMedia.IsEnabled = canControl && SelectedDeviceFilePath() is not null;
    }

    /// <summary>切换存储卷时清除旧列表。</summary>
    private void OnDeviceMediaFolderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceMediaFiles is not null)
            ClearDeviceMediaList();
        UpdateDeviceMediaControls();
    }

    /// <summary>选中文件变化时重新计算操作可用性。</summary>
    private void OnDeviceMediaSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDeviceMediaControls();

    /// <summary>读取设备目录并显示完整或部分文件列表。</summary>
    private async void OnRefreshDeviceMedia(object sender, RoutedEventArgs e)
    {
        if (busy || GetDeviceMediaDirectory() is not { } directory)
            return;

        ClearDeviceMediaList();
        ShowDeviceMediaMessage(T("ReadingDeviceFiles"));
        IReadOnlyList<string> files = [];
        CancellationToken cancellation = deviceControlShutdown.Token;
        await RunDeviceControlAsync(T("ReadingDeviceFiles"), session =>
        {
            try
            {
                (files, bool complete) = session.ReadDeviceDirectory(directory, cancellation);
                // 回包不完整时仍展示已解析的文件。
                if (!complete)
                    return T("DirectoryPartialResults");
                return files.Count == 0 ? T("DeviceDirectoryEmpty") : T("FoundDeviceFiles", files.Count);
            }
            catch (DeviceResponseException error) when (error.Failure == DeviceResponseFailure.DirectoryCreationFailed)
            {
                return T("ReadDeviceDirectoryFailed", DescribeUiError(error));
            }
        }, message =>
        {
            DeviceMediaFiles.ItemsSource = files;
            ShowDeviceMediaMessage(message);
            return message;
        }, onFailure: error => ShowDeviceMediaMessage(T("ReadDeviceDirectoryFailed", DescribeUiError(error))));
    }

    /// <summary>确认后删除所选设备文件，并重新查询目录。</summary>
    private async void OnDeleteDeviceMedia(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedDevice is not { } device || GetDeviceMediaDirectory() is not { } directory
            || SelectedDeviceFilePath() is not { } path)
            return;

        string file = path[directory.Length..];
        if (!await ConfirmAsync(T("DeleteSelected"), T("ConfirmDeleteDeviceFile", device.Model.Name, file))
            || busy || SelectedDevice?.Path != device.Path || SelectedDeviceFilePath() != path)
            return;

        IReadOnlyList<string> files = [];
        CancellationToken cancellation = deviceControlShutdown.Token;
        await RunDeviceControlAsync(T("DeletingDeviceFile"), session =>
        {
            session.DeleteDeviceFile(path);
            try
            {
                (files, bool complete) = session.ReadDeviceDirectory(directory, cancellation);
                if (files.Contains(file, StringComparer.Ordinal))
                    return T("DeviceFileStillListed");
                return complete ? T("DeletedFileNotListed") : T("DirectoryIncompleteVerifyDeletion");
            }
            catch (DeviceResponseException error) when (error.Failure == DeviceResponseFailure.DirectoryCreationFailed)
            {
                return T("VerifyFileDeletionFailed", DescribeUiError(error));
            }
        }, message =>
        {
            DeviceMediaFiles.ItemsSource = files;
            ShowDeviceMediaMessage(message);
            return message;
        }, onFailure: error =>
        {
            ClearDeviceMediaList();
            ShowDeviceMediaMessage(T("VerifyFileDeletionFailed", DescribeUiError(error)));
        });
    }

    /// <summary>停止冲突的设备播放状态后播放所选文件。</summary>
    private async void OnPlayDeviceMedia(object sender, RoutedEventArgs e)
    {
        if (busy || !CanPlayDeviceMedia() || GetDeviceMediaDirectory() is not { } directory
            || DeviceMediaFiles.SelectedItem is not string file)
            return;

        await RunDeviceControlAsync(T("StartingOnDevicePlayback"), session =>
        {
            session.PlayDeviceFile(directory + file);
            return T("PlayingOnDeviceFile", file);
        }, message =>
        {
            StopLocalPreview();
            HomeCanvas.StopVideo();
            return message;
        });
    }
}
