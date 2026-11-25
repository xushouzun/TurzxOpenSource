using System.IO;
using TURZX.WinUsbDisplay.Studio;
using Xunit;

namespace TURZX.WinUsbDisplay.Windows.Tests;

public class DesktopModeDriverTests
{
    [Fact]
    public void LeftoverUmdfCopyDoesNotCountAsInstalled()
    {
        using var temp = new TempDirectory();
        string umdf = Path.Combine(temp.Path, "drivers", "UMDF");
        Directory.CreateDirectory(umdf);
        File.WriteAllBytes(Path.Combine(umdf, "TURZX_display_driver.dll"), []);
        Assert.False(DesktopModeDriver.IsInstalledIn(temp.Path));
    }

    [Fact]
    public void DriverStorePackageMarksDriverAsInstalled()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "DriverStore", "FileRepository",
            "turzx_display_driver.inf_amd64_1234"));
        Assert.True(DesktopModeDriver.IsInstalledIn(temp.Path));
    }

    [Fact]
    public void EmptySystemDirectoryHasNoDriver()
    {
        using var temp = new TempDirectory();
        Assert.False(DesktopModeDriver.IsInstalledIn(temp.Path));
        Assert.False(DesktopModeDriver.IsInstalledIn(""));
    }

    [Fact]
    public void PublishedNamesAreExtractedFromEnumerationOutput()
    {
        const string output = """
            Published Name:     oem12.inf
            Original Name:      other_driver.inf
            Provider Name:      Other

            Published Name:     oem66.inf
            Original Name:      turzx_display_driver.inf
            Provider Name:      TURZX Display

            """;
        Assert.Equal(new[] { "oem66.inf" }, DesktopModeDriver.ParsePublishedNames(output));
    }

    [Fact]
    public void EnumerationWithoutTheBundledDriverYieldsNoNames()
    {
        const string output = "Published Name: oem12.inf\nOriginal Name: other_driver.inf\n";
        Assert.Empty(DesktopModeDriver.ParsePublishedNames(output));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "turzx-driver-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
