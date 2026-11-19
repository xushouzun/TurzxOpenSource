using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using TURZX.WinUsbDisplay.Studio;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class CommandPageSelectionTests
{
    [Fact]
    public void EachCommandSelectionUpdatesTheRealStudioForm()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Instantiate the actual WPF page without showing it or opening a device.
                var window = new MainWindow(new StudioPreferences(Path.Combine(Path.GetTempPath(),
                    $"turzx-test-{Guid.NewGuid():N}.json")));
                try
                {
                    window.CommandsNav.IsChecked = true;
                    Assert.Equal(40, window.CommandList.Items.OfType<DeviceCommand>().Count());

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
                        Assert.False(window.SendPacket.IsEnabled);
                    }

                    // Same command byte, distinct forms and descriptions for each channel.
                    window.PacketId.Text = "0x62";
                    Assert.Equal("GET_COOLING_SPEEDS", Assert.IsType<DeviceCommand>(window.CommandList.SelectedItem).ProtocolName);
                    window.PacketChannel.SelectedIndex = 0;
                    Assert.Equal("GET_DEVICE_FILE_SIZE", Assert.IsType<DeviceCommand>(window.CommandList.SelectedItem).ProtocolName);
                    Assert.Equal(2, window.PacketArgumentsMode.SelectedIndex);
                }
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
