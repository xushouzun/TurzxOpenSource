using System.Diagnostics;
using System.Text;
using TURZX.WinUsbDisplay.Media;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

public class ProcessDiagnosticsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    [InlineData(65536)]
    [InlineData(1000000)]
    public async Task DiagnosticsPreserveShortOutputAndBoundLongOutput(int size)
    {
        string text = new('x', size);
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var diagnostics = new ProcessDiagnostics(reader);
        await diagnostics.Completion;
        if (size <= ProcessDiagnostics.MaximumCharacters)
            Assert.Equal(text, diagnostics.Text);
        else
        {
            Assert.True(diagnostics.Text.Length <= ProcessDiagnostics.MaximumCharacters + 128);
            Assert.Contains("[diagnostics truncated]", diagnostics.Text);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task ConverterDrainsLargeDiagnosticsAndRetainsMetadataAndFinalError(int exitCode)
    {
        if (!OperatingSystem.IsWindows())
            return;
        const string header = "Stream #0:1: Video: h264, 320x240, 24 fps";
        const string marker = "final-error-marker";
        string script = $"[Console]::Error.WriteLine('{header}'); "
            + "[Console]::Error.Write(('x' * 1000000)); "
            + $"[Console]::Error.WriteLine('\nStream #0:1 -> #0:0\n{marker}'); exit {exitCode}";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script.Replace("\\n", "`n"))) })
            start.ArgumentList.Add(arg);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string text;
        if (exitCode == 0)
            text = await VideoConverter.RunAsync(start, deadline.Token);
        else
        {
            var error = await Assert.ThrowsAsync<FfmpegProcessException>(() => VideoConverter.RunAsync(start, deadline.Token));
            Assert.Equal(exitCode, error.ExitCode);
            text = error.Diagnostics;
        }
        Assert.True(text.Length <= ProcessDiagnostics.MaximumCharacters + 128);
        Assert.Contains(header, text);
        Assert.EndsWith(marker, text.TrimEnd());
        Assert.Equal(24, VideoConverter.ParseSelectedFrameRate(text));
    }
}
