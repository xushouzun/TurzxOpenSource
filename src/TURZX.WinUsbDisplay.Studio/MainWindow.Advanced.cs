using System.ComponentModel;
using System.IO;
using System.Windows;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 实现设备高级状态查询和固件时钟控制。
public partial class MainWindow
{
    /// <summary>最近一次刷新高级控件时所选设备的接口路径。</summary>
    private string? advancedDevicePath;

    /// <summary>检查所选型号是否支持固件内置时钟。</summary>
    private static bool SupportsFirmwareClock(DisplayModel? model) =>
        model?.ProductId is 0x0046 or 0x0123;

    /// <summary>清空高级面板中此前显示的设备状态。</summary>
    private void ResetAdvancedStatus()
    {
        AdvancedStatusResult.Text = SelectedDevice?.Model.ProductId switch
        {
            null => T("SelectDisplayForStatus"),
            0x0123 => T("StatusIncludesVoltageHint"),
            _ => T("StatusReadHint")
        };
    }

    /// <summary>根据所选设备和操作状态更新高级面板。</summary>
    private void UpdateAdvancedControls()
    {
        if (ReadAdvancedStatus is null)
            return;

        var device = SelectedDevice;
        if (!string.Equals(advancedDevicePath, device?.Path, StringComparison.OrdinalIgnoreCase)
            || AdvancedStatusResult.Text.Length == 0)
        {
            advancedDevicePath = device?.Path;
            ResetAdvancedStatus();
            ResetDeviceMediaDirectory(device);
            LoadStartupSettings(device);
        }

        ReadAdvancedStatus.IsEnabled = !busy && device is not null;
        AdvancedClockPanel.Visibility = SupportsFirmwareClock(device?.Model) ? Visibility.Visible : Visibility.Collapsed;
        AdvancedClockPanel.IsEnabled = !busy;
        UpdateSendBenchmarkControls();
        UpdateDeviceMediaControls();
        UpdateDeviceSettingsControls(device);
    }

    /// <summary>查询并显示固件版本、电压和存储容量。</summary>
    private async void OnReadAdvancedStatus(object sender, RoutedEventArgs e)
    {
        if (busy || SelectedDevice is not { Model: var model })
            return;

        ResetAdvancedStatus();
        bool partial = false;
        CancellationToken cancellation = deviceControlShutdown.Token;
        await RunDeviceControlAsync(T("ReadingDeviceStatus"), session =>
        {
            Exception? firstError = null;
            int available = 0;

            // 读取一项设备状态，并保留对应查询的错误信息。
            string Read(string label, Func<string> query)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    string value = query();
                    available++;
                    return $"{label}  {value}";
                }
                catch (Exception ex) when (ex is IOException or Win32Exception)
                {
                    firstError ??= ex;
                    return T("Unavailable", label);
                }
            }

            var lines = new List<string>
            {
                Read(T("FirmwareVersion"), () => session.ReadFirmwareVersion(cancellation)),
                Read(T("Storage"), () => Environment.NewLine + DeviceCommands.DescribeStorage(session.ReadStorageCapacity(cancellation)))
            };
            if (model.ProductId == 0x0123)
                lines.Add(Read(T("Power"), () => $"{session.ReadSupplyVoltageMillivolts(cancellation)} mV"));

            if (available == 0 && firstError is not null)
                throw UiError(new IOException("Could not read device status.", firstError), "ErrorReadDeviceStatus");

            partial = available != lines.Count;
            return string.Join(Environment.NewLine, lines);
        }, result =>
        {
            AdvancedStatusResult.Text = result;
            return partial ? T("SomeDeviceStatusFieldsAreUnavailable") : T("DeviceStatusUpdated");
        });
    }

    /// <summary>开启设备固件时钟图层。</summary>
    private async void OnShowAdvancedClock(object sender, RoutedEventArgs e) => await SetFirmwareClockAsync(true);

    /// <summary>关闭设备固件时钟图层。</summary>
    private async void OnStopAdvancedClock(object sender, RoutedEventArgs e) => await SetFirmwareClockAsync(false);

    /// <summary>向所选设备发送固件时钟控制命令。</summary>
    private Task SetFirmwareClockAsync(bool enabled)
    {
        if (!SupportsFirmwareClock(SelectedDevice?.Model))
            return Task.CompletedTask;

        return RunDeviceControlAsync(T("SettingDeviceClock"), session =>
        {
            session.ShowDefaultClock(enabled);
            return enabled ? T("ClockShowCommandSent") : T("ClockHideCommandSent");
        });
    }

}
