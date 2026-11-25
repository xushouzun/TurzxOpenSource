using System.ComponentModel;
using System.IO;
using System.Windows;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

// 实现设备高级状态查询、固件时钟控制和桌面模式驱动安装。
public partial class MainWindow
{
    /// <summary>最近一次刷新高级控件时所选设备的接口路径。</summary>
    private string? advancedDevicePath;
    private bool installingDesktopModeDriver;
    private bool uninstallingDesktopModeDriver;
    private bool desktopModeDriverInstalled;

    /// <summary>检查所选型号是否支持固件内置时钟。</summary>
    private static bool SupportsFirmwareClock(DisplayModel? model) =>
        model is { ProductId: 0x0046 } && DeviceCommands.CanSend(model.ProductId, 51, raw: false)
            && DeviceCommands.CanSend(model.ProductId, 52, raw: false);

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
        UpdateDesktopModeDriverControls();
    }

    /// <summary>重新检测桌面模式驱动是否已安装。</summary>
    private void RefreshDesktopModeDriverStatus()
    {
        if (InstallDesktopModeDriver is null || UninstallDesktopModeDriver is null || DesktopModeDriverStatus is null) return;
        desktopModeDriverInstalled = DesktopModeDriver.IsInstalled;
        UpdateDesktopModeDriverControls();
    }

    /// <summary>同步驱动安装、卸载按钮和状态文案。</summary>
    private void UpdateDesktopModeDriverControls()
    {
        if (InstallDesktopModeDriver is null || UninstallDesktopModeDriver is null || DesktopModeDriverStatus is null) return;
        bool changing = installingDesktopModeDriver || uninstallingDesktopModeDriver;
        InstallDesktopModeDriver.IsEnabled = !busy && !changing
            && DesktopModeDriver.IsAvailable && !desktopModeDriverInstalled;
        UninstallDesktopModeDriver.IsEnabled = !busy && !changing
            && DesktopModeDriver.IsAvailable && desktopModeDriverInstalled;
        if (changing) return;
        DesktopModeDriverStatus.Text = !DesktopModeDriver.IsAvailable
            ? T("DesktopModeDriverUnavailable")
            : desktopModeDriverInstalled ? T("DesktopModeDriverInstalled") : T("DesktopModeDriverMissing");
    }

    /// <summary>提权安装随包的桌面模式显示驱动。</summary>
    private async void OnInstallDesktopModeDriver(object sender, RoutedEventArgs e)
    {
        if (busy || installingDesktopModeDriver || uninstallingDesktopModeDriver || !DesktopModeDriver.IsAvailable) return;
        installingDesktopModeDriver = true;
        UpdateDesktopModeDriverControls();
        DesktopModeDriverStatus.Text = T("DesktopModeDriverInstalling");
        try
        {
            bool installed = await DesktopModeDriver.InstallAsync(deviceControlShutdown.Token);
            if (!installed)
                Status.Text = T("DesktopModeDriverInstallFailed");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            Status.Text = T("DesktopModeDriverCancelled");
        }
        catch (OperationCanceledException) when (deviceControlShutdown.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Status.Text = DescribeUiError(error);
        }
        finally
        {
            installingDesktopModeDriver = false;
            if (!closing)
                RefreshDesktopModeDriverStatus();
        }
    }

    /// <summary>提权卸载桌面模式显示驱动。</summary>
    private async void OnUninstallDesktopModeDriver(object sender, RoutedEventArgs e)
    {
        if (busy || installingDesktopModeDriver || uninstallingDesktopModeDriver || !desktopModeDriverInstalled) return;
        if (MessageBox.Show(T("DesktopModeDriverConfirmUninstall"), T("UninstallDesktopModeDriver"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        uninstallingDesktopModeDriver = true;
        UpdateDesktopModeDriverControls();
        DesktopModeDriverStatus.Text = T("DesktopModeDriverUninstalling");
        try
        {
            bool uninstalled = await DesktopModeDriver.UninstallAsync(deviceControlShutdown.Token);
            Status.Text = uninstalled
                ? T("DesktopModeDriverUninstalled")
                : T("DesktopModeDriverUninstallFailed");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            Status.Text = T("DesktopModeDriverUninstallCancelled");
        }
        catch (OperationCanceledException) when (deviceControlShutdown.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Status.Text = DescribeUiError(error);
        }
        finally
        {
            uninstallingDesktopModeDriver = false;
            if (!closing)
                RefreshDesktopModeDriverStatus();
        }
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
