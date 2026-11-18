using TURZX.WinUsbDisplay.Studio;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class StudioPreferencesTests
{
    [Theory]
    [InlineData("{ broken settings")]
    [InlineData("null")]
    public void SavingAfterCorruptionPreservesOriginalForRecovery(string damaged)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        File.WriteAllText(path, damaged);
        var preferences = new StudioPreferences(path);
        Assert.Equal("zh-CN", preferences.Language);

        preferences.SetLanguage("ja-JP");

        Assert.Equal("ja-JP", new StudioPreferences(path).Language);
        string backup = Assert.Single(Directory.GetFiles(directory.Path, "settings.json.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllText(backup));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void StaleInstancesMergeUnrelatedSettingsWithoutLosingChanges()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        var first = new StudioPreferences(path);
        var second = new StudioPreferences(path);

        first.SetLanguage("ko-KR");
        second.UpdateScreen("device-a", screen => screen with { Brightness = 37, Rotation = 2 });
        first.SetMedia(new MediaPreferences { DevicePath = "device-b", ImagePath = "saved.png" });

        var saved = new StudioPreferences(path);
        Assert.Equal("ko-KR", saved.Language);
        Assert.Equal((byte)37, saved.GetScreen("DEVICE-A").Brightness);
        Assert.Equal((byte)2, saved.GetScreen("device-a").Rotation);
        Assert.Equal("saved.png", saved.Media?.ImagePath);
        Assert.Equal("saved.png", saved.GetDeviceMedia("DEVICE-B")?.ImagePath);
    }

    [Fact]
    public void UpdatingOneScreenPreservesOtherDevicesAndItsUnchangedFields()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        var first = new StudioPreferences(path);
        var second = new StudioPreferences(path);
        first.UpdateScreen("first", screen => screen with { Brightness = 45, Rotation = 3 });
        second.UpdateScreen("second", screen => screen with { Brightness = 72 });
        second.UpdateScreen("FIRST", screen => screen with { StartupMode = 2, KeepAwakeOffline = true });

        var saved = new StudioPreferences(path);
        var screen = saved.GetScreen("first");
        Assert.Equal((byte)45, screen.Brightness);
        Assert.Equal((byte)3, screen.Rotation);
        Assert.Equal((byte)2, screen.StartupMode);
        Assert.True(screen.KeepAwakeOffline);
        Assert.Equal((byte)72, saved.GetScreen("second").Brightness);
    }

    [Fact]
    public void OutOfRangeScreenSettingsFallBackIndependently()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        File.WriteAllText(path, """
            {"Screens":{"DEVICE":{"Brightness":255,"Rotation":255,"StartupMode":255,
            "OfflineClockRotation":255,"KeepAwakeOffline":true}}}
            """);

        var screen = new StudioPreferences(path).GetScreen("device");

        Assert.Equal((byte)66, screen.Brightness);
        Assert.Equal((byte)0, screen.Rotation);
        Assert.Equal((byte)0, screen.StartupMode);
        Assert.Equal((byte)0, screen.OfflineClockRotation);
        Assert.True(screen.KeepAwakeOffline);
    }

    [Fact]
    public void NullCollectionsAndScreenEntriesRemainUsable()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        File.WriteAllText(path, """{"DeviceMedia":null,"Screens":{"DEVICE":null}}""");
        var preferences = new StudioPreferences(path);
        Assert.Equal((byte)66, preferences.GetScreen("device").Brightness);
        Assert.Null(preferences.GetDeviceMedia("device"));

        preferences.UpdateScreen("device", screen => screen with { Brightness = 50 });
        preferences.SetMedia(new MediaPreferences { DevicePath = "device", HtmlPath = "scene.html" });

        var saved = new StudioPreferences(path);
        Assert.Equal((byte)50, saved.GetScreen("device").Brightness);
        Assert.Equal("scene.html", saved.GetDeviceMedia("device")?.HtmlPath);
    }

    [Fact]
    public void FailedUpdateLeavesPreviouslySavedSettingsIntact()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("settings.json");
        var preferences = new StudioPreferences(path);
        preferences.SetLanguage("en-US");
        string original = File.ReadAllText(path);

        Assert.Throws<InvalidOperationException>(() => preferences.UpdateScreen("device", _ =>
            throw new InvalidOperationException("Rejected update")));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal("en-US", preferences.Language);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}
