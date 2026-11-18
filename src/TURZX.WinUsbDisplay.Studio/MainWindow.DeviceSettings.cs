using System.Windows;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 管理设备持久化启动设置和重启操作。
public partial class MainWindow
{
    /// <summary>检查所选设备型号是否支持写入启动配置。</summary>
    private static bool SupportsStartupSettings(DisplayModel? model) =>
        model?.ProductId is 0x0046 or 0x0123;

    /// <summary>从按设备保存的首选项载入启动设置。</summary>
    private void LoadStartupSettings(DisplayDevice? device)
    {
        var settings = device is null ? new ScreenPreferences() : preferences.GetScreen(device.Path);
        StartupModeSelect.SelectedIndex = settings.StartupMode;
        KeepAwakeOffline.IsChecked = settings.KeepAwakeOffline;
        OfflineClockDirectionSelect.SelectedIndex = settings.OfflineClockRotation;
    }

    /// <summary>根据设备能力和忙碌状态设置操作按钮。</summary>
    private void UpdateDeviceSettingsControls(DisplayDevice? device)
    {
        StartupSettingsPanel.Visibility = SupportsStartupSettings(device?.Model) ? Visibility.Visible : Visibility.Collapsed;
        StartupSettingsPanel.IsEnabled = !busy;
        OfflineClockDirectionPanel.Visibility = device?.Model.ProductId == 0x0123 ? Visibility.Visible : Visibility.Collapsed;
        RestartDeviceButton.IsEnabled = !busy && device is not null;
    }

    /// <summary>向设备写入启动配置并保存本地设置。</summary>
    private async void OnSaveStartupSettings(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedDevice is not { } device || !SupportsStartupSettings(device.Model))
            return;

        var settings = preferences.GetScreen(device.Path) with
        {
            StartupMode = (byte)StartupModeSelect.SelectedIndex,
            KeepAwakeOffline = KeepAwakeOffline.IsChecked == true,
            OfflineClockRotation = device.Model.ProductId == 0x0123 ? (byte)OfflineClockDirectionSelect.SelectedIndex : (byte)0
        };
        if (!await ConfirmAsync(T("WriteStartupSettings"),
                T("ConfirmOverwriteStartupSettings", device.Model.Name))
            || busy || SelectedDevice?.Path != device.Path)
            return;

        await RunDeviceControlAsync(T("WritingStartupSettings"), session =>
        {
            session.SetStartupConfiguration(settings.Brightness, settings.StartupMode, settings.Rotation,
                settings.KeepAwakeOffline, settings.OfflineClockRotation);
            return T("StartupSettingsSent");
        }, message =>
        {
            preferences.UpdateScreen(device.Path, latest => latest with
            {
                StartupMode = settings.StartupMode,
                KeepAwakeOffline = settings.KeepAwakeOffline,
                OfflineClockRotation = settings.OfflineClockRotation
            });
            return message;
        });
    }

    /// <summary>确认后向当前设备发送重启命令。</summary>
    private async void OnRestartDevice(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedDevice is not { } device)
            return;
        if (!await ConfirmAsync(T("RestartDisplay"), T("ConfirmRestartDisplay", device.Model.Name))
            || busy || SelectedDevice?.Path != device.Path)
            return;

        await RunDeviceControlAsync(T("RestartingTheDisplay"), session =>
        {
            session.RestartDevice();
            return T("DisplayRestartCommandSent");
        }, message =>
        {
            ClearDeviceMediaList();
            ResetAdvancedStatus();
            return message;
        });
    }
}
