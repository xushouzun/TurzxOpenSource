using System.Windows;
using System.Windows.Controls;
using static TURZX.WinUsbDisplay.Studio.StudioLanguage;

namespace TURZX.WinUsbDisplay.Studio;

public partial class MainWindow
{
    internal sealed class DesktopRecoveryEntry
    {
        public string ProtocolName => "SWITCH_TO_MONITOR_MODE";
        public string CodeLabel => "HID";
        public string Purpose => T("ReturnToMonitor");
    }

    private readonly DesktopRecoveryEntry desktopRecoveryEntry = new();
    private IEnumerable<object> CommandEntries
    {
        get
        {
            foreach (DeviceCommand command in DeviceCommands.All)
            {
                yield return command;
                if (command is { Code: 150, Raw: false })
                    yield return desktopRecoveryEntry;
            }
        }
    }
    private bool IsDesktopRecoverySelected => ReferenceEquals(CommandList.SelectedItem, desktopRecoveryEntry);
    private bool scanningDesktopDevices;

    private async Task RefreshDesktopDevicesAsync()
    {
        if (closing || scanningDesktopDevices || DesktopModeDevices is null) return;
        scanningDesktopDevices = true;
        RefreshDesktopDevices.IsEnabled = ReturnToMonitor.IsEnabled = false;
        try
        {
            string? serial = (DesktopModeDevices.SelectedItem as DesktopModeDevice)?.Serial;
            var found = await Task.Run(DesktopModeDevice.Discover);
            if (closing) return;
            DesktopModeDevices.ItemsSource = found;
            DesktopModeDevices.SelectedItem = found.FirstOrDefault(d => d.Serial == serial);
            if (DesktopModeDevices.SelectedItem is null && found.Count == 1) DesktopModeDevices.SelectedIndex = 0;
            DesktopModeStatus.SetResourceReference(TextBlock.TextProperty,
                ResourceKey(found.Count == 0 ? "DesktopModeNone" : "DesktopModeSelectTarget"));
        }
        catch (Exception error)
        {
            if (!closing) DesktopModeStatus.Text = DescribeUiError(error);
        }
        finally
        {
            scanningDesktopDevices = false;
            if (!closing) UpdateDesktopModeControls();
        }
    }

    private void UpdateDesktopModeControls()
    {
        if (ReturnToMonitor is null || RefreshDesktopDevices is null || DesktopModeDevices is null) return;
        RefreshDesktopDevices.IsEnabled = !busy && !scanningDesktopDevices;
        ReturnToMonitor.IsEnabled = !busy && !scanningDesktopDevices && DesktopModeDevices.SelectedItem is DesktopModeDevice;
    }

    private async void OnRefreshDesktopDevices(object sender, RoutedEventArgs e) => await RefreshDesktopDevicesAsync();
    private void OnDesktopModeSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDesktopModeControls();

    private async void OnReturnToMonitor(object sender, RoutedEventArgs e)
    {
        if (busy || DesktopModeDevices.SelectedItem is not DesktopModeDevice target) return;
        string? result = null;
        await RunOperationAsync(async () =>
        {
            DesktopModeStatus.Text = T("DesktopModeSwitching", target.Serial);
            await target.SwitchToMonitorAsync(deviceControlShutdown.Token);
            // 只枚举接口确认恢复，不追加 Win207 控制指令。
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(25))
            {
                deviceControlShutdown.Token.ThrowIfCancellationRequested();
                var found = await Task.Run(() => DisplayDevice.Discover(DisplaySettings.InterfaceGuid));
                var restored = found.FirstOrDefault(d => d.Path.Contains("#" + target.Serial + "#", StringComparison.OrdinalIgnoreCase));
                if (restored is not null)
                {
                    preferredDevicePath = restored.Path;
                    refreshAfterOperation = true;
                    result = T("DesktopModeRestored", target.Serial);
                    DesktopModeDevices.ItemsSource = null;
                    return;
                }
                await Task.Delay(500, deviceControlShutdown.Token);
            }
            result = T("DesktopModeNotRestored");
        }, T("DesktopModeSwitching", target.Serial), onFailure: error => result = DescribeUiError(error));
        await RefreshDesktopDevicesAsync();
        if (!closing && result is not null)
            DesktopModeStatus.Text = Status.Text = result;
    }
}
