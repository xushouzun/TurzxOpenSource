using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using TURZX.WinUsbDisplay.Studio;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class CommandPageSelectionTests
{
    [Fact]
    public void EachCommandSelectionUpdatesTheRealStudioForm()
    {
        WithStudioWindow(window =>
        {
            window.CommandsNav.IsChecked = true;
            Assert.Equal(40, window.CommandList.Items.OfType<DeviceCommand>().Count());
            Assert.Same(DeviceCommands.All[0], window.CommandList.Items[0]);
            int desktopModeIndex = window.CommandList.Items.IndexOf(
                DeviceCommands.All.Single(command => command is { Code: 150, Raw: false }));
            var recoveryEntry = Assert.Single(window.CommandList.Items.Cast<object>(),
                entry => entry is not DeviceCommand);
            Assert.Same(recoveryEntry, window.CommandList.Items[desktopModeIndex + 1]);
            var recovery = Assert.IsType<MainWindow.DesktopRecoveryEntry>(recoveryEntry);
            Assert.Equal("SWITCH_TO_MONITOR_MODE", recovery.ProtocolName);
            Assert.Equal("HID", recovery.CodeLabel);
            Assert.Equal(StudioLanguage.T("ReturnToMonitor"), recovery.Purpose);

            foreach (var command in window.CommandList.Items.OfType<DeviceCommand>().ToArray())
            {
                window.CommandList.SelectedItem = null;
                window.CommandList.SelectedItem = command;

                string id = command.Raw ? $"0x{command.Code:X2}" : command.Code.ToString(CultureInfo.InvariantCulture);
                Assert.Equal(id, window.PacketId.Text);
                Assert.Equal(command.Raw ? 1 : 0, window.PacketChannel.SelectedIndex);
                Assert.Equal($"{command.Purpose}\n{command.Detail}", window.CommandResult.Text);
                Assert.Equal(Visibility.Collapsed, window.DesktopRecoveryPanel.Visibility);
                var (arguments, payload) = DeviceCommands.GetRequestFields((byte)command.Code, command.Raw);
                Assert.Equal(arguments ? Visibility.Visible : Visibility.Collapsed, window.PacketArgumentsRow.Visibility);
                Assert.Equal(payload ? Visibility.Visible : Visibility.Collapsed, window.PacketPayloadRow.Visibility);
                if (!command.Raw && command.Code == 51)
                    Assert.Equal(Visibility.Collapsed, window.RunCommand.Visibility);
                Assert.False(window.SendPacket.IsEnabled);
            }

            // Same command byte, distinct forms and descriptions for each channel.
            window.PacketId.Text = "0x62";
            Assert.Equal("GET_COOLING_SPEEDS", Assert.IsType<DeviceCommand>(window.CommandList.SelectedItem).ProtocolName);
            window.PacketChannel.SelectedIndex = 0;
            Assert.Equal("GET_DEVICE_FILE_SIZE", Assert.IsType<DeviceCommand>(window.CommandList.SelectedItem).ProtocolName);
            Assert.Equal(2, window.PacketArgumentsMode.SelectedIndex);
            window.OpenDocumentationLink("https://example.invalid/documentation",
                _ => throw new System.ComponentModel.Win32Exception(1155));
            Assert.Equal(StudioLanguage.T("CouldNotOpenDocumentationLink"), window.Status.Text);
        });
    }

    [Fact]
    public void RestoredDesktopDisplayIsSelectedEvenWhenTheSnapshotHasNotChanged()
    {
        WithStudioWindow(window =>
        {
            var first = new DisplayDevice("first", DisplayModel.Known[0]);
            var restored = new DisplayDevice("restored", DisplayModel.Known[1]);

            window.ApplyDeviceSnapshot([first, restored]);
            Assert.Equal(0, window.WorkspaceDevice.SelectedIndex);

            window.ApplyDeviceSnapshot([first, restored], selectDevicePath: restored.Path);
            Assert.Equal(1, window.WorkspaceDevice.SelectedIndex);

            window.ApplyDeviceSnapshot([first, restored]);
            Assert.Equal(1, window.WorkspaceDevice.SelectedIndex);
        });
    }

    [Fact]
    public void ReconnectingResumesPlaybackAfterTheOldPlaybackHasStopped()
    {
        var original = new DisplayDevice("test-device", DisplayModel.Known[0]);
        var reconnected = original with { Path = "TEST-DEVICE" };
        DisplayDevice[] snapshot = [original];
        WithStudioWindow(window =>
        {
            RunOnDispatcher(window, CheckAsync);

            async Task CheckAsync()
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var resumed = new TaskCompletionSource<DisplayDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool stopped = false;
                int attempts = 0;

                await window.RefreshDevicesAsync();
                var playback = window.RunPlaybackAsync(original, async active =>
                {
                    if (++attempts > 1)
                    {
                        Assert.True(stopped);
                        resumed.TrySetResult(active.Device);
                        return;
                    }
                    active.VideoFps = 30;
                    started.SetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, active.Token); }
                    finally
                    {
                        await finishCleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        stopped = true;
                    }
                }, "Starting playback", resumeOnReconnect: true);

                await started.Task;
                try
                {
                    Volatile.Write(ref snapshot, []);
                    await window.RefreshDevicesAsync();
                    Assert.Equal(StudioLanguage.T("WaitingToReconnect"), window.PlaybackIndicator.Text);

                    Volatile.Write(ref snapshot, [reconnected]);
                    await window.RefreshDevicesAsync();
                    Assert.Equal(1, attempts);
                }
                finally { finishCleanup.TrySetResult(); }
                await playback;
                Assert.Same(reconnected, await resumed.Task);
                Assert.Equal(2, attempts);
                Assert.Equal(0, window.WorkspaceDevice.SelectedIndex);
            }
        }, () => Volatile.Read(ref snapshot));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenericWinUsbTransferFailureResumesOnlyWhenTheDeviceDisappeared(bool unplugged)
    {
        var original = new DisplayDevice("0070-test", DisplayModel.Known.Single(model => model.ProductId == 0x0070));
        DisplayDevice[] snapshot = [original];
        WithStudioWindow(window => RunOnDispatcher(window, async () =>
        {
            int attempts = 0;
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await window.RefreshDevicesAsync();
            await window.RunPlaybackAsync(original, active =>
            {
                if (++attempts == 1)
                {
                    active.VideoFps = 30;
                    if (unplugged)
                        Volatile.Write(ref snapshot, []);
                    throw new AggregateException(new System.ComponentModel.Win32Exception(31, "WinUsb_WritePipe: test error"));
                }
                resumed.SetResult();
                return Task.CompletedTask;
            }, "Starting playback", resumeOnReconnect: true);

            if (unplugged)
            {
                Assert.Equal(StudioLanguage.T("WaitingToReconnect"), window.PlaybackIndicator.Text);
                var reconnected = original with { Path = "0070-TEST" };
                Volatile.Write(ref snapshot, [reconnected]);
                await window.RefreshDevicesAsync();
                await resumed.Task;
                Assert.Equal(2, attempts);
            }
            else
            {
                await window.RefreshDevicesAsync();
                Assert.Contains("31", window.PlaybackIndicator.Text);
                Assert.Equal(1, attempts);
            }
        }), () => Volatile.Read(ref snapshot));
    }

    private static void RunOnDispatcher(MainWindow window, Func<Task> check)
    {
        var dispatcher = window.Dispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var frame = new DispatcherFrame();
        Task pending = check().WaitAsync(TimeSpan.FromSeconds(10));
        _ = pending.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        pending.GetAwaiter().GetResult();
    }

    private static void WithStudioWindow(Action<MainWindow> check, Func<DisplayDevice[]>? discoverDisplays = null)
    {
        using var directory = new TemporaryDirectory();
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Instantiate the actual WPF window without showing it or opening a device.
                var window = new MainWindow(new StudioPreferences(directory.File("settings.json")), discoverDisplays);
                try { check(window); }
                finally { window.Close(); }
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Studio command form did not initialize.");
        failure?.Throw();
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    public void CommandDetailsExistInEachBundledLanguage(string language)
    {
        using var catalog = typeof(StudioLanguage).Assembly.GetManifestResourceStream($"Studio.Localization.{language}.json");
        Assert.NotNull(catalog);
        using var translations = JsonDocument.Parse(catalog);
        foreach (var command in DeviceCommands.All)
        {
            string key = $"CommandDetail.{command.ProtocolName}";
            Assert.True(translations.RootElement.TryGetProperty(key, out var detail), $"{language}: {key}");
            Assert.False(string.IsNullOrWhiteSpace(detail.GetString()), $"{language}: {key}");
        }
    }
}
