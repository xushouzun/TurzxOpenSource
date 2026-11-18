using System.Diagnostics;
using System.Text;
using TURZX.WinUsbDisplay.Media;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

public class BrowserDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFailureBoundsDiagnosticsAndDoesNotWaitForInheritedPipe(bool inheritPipe)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var directory = Directory.CreateTempSubdirectory("BrowserDiagnostics-");
        string childId = Path.Combine(directory.FullName, "child.pid");
        string childScript = Path.Combine(directory.FullName, "child.ps1");
        string parentScript = Path.Combine(directory.FullName, "browser.cmd");
        string outputComplete = Path.Combine(directory.FullName, "output.complete");
        const string header = "browser-startup-header";
        const string tail = "browser-final-error";
        string writer = $"[Console]::Error.WriteLine('{header}'); "
            + "[Console]::Error.Write(('x' * 1000000)); "
            + $"[Console]::Error.WriteLine('{tail}'); "
            + $"[IO.File]::WriteAllText('{outputComplete.Replace("'", "''")}', 'done'); exit 7";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await File.WriteAllTextAsync(childScript,
                $"[IO.File]::WriteAllText('{childId.Replace("'", "''")}', [string]$PID)\nStart-Sleep -Seconds 60\n");
            string child = inheritPipe
                ? $"start \"\" /b powershell.exe -NoProfile -WindowStyle Hidden -File \"{childScript}\"\r\n"
                    + $":wait\r\nif not exist \"{childId}\" goto wait\r\n"
                : "";
            await File.WriteAllTextAsync(parentScript, "@echo off\r\n" + child
                + "powershell.exe -NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(writer)) + "\r\nexit /b 7\r\n");
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                Arguments = $"/d /c \"\"{parentScript}\"\""
            };
            await using var capture = new HtmlCapture();
            Task connecting = capture.ConnectAsync(
                new Uri("https://example.invalid/"), DisplayModel.Known[0], deadline.Token,
                fastCapture: false, pixelScale: 1, browserStart: start);
            // 从输出结束开始计时，避免将并行构建时 PowerShell 的启动开销算作管道收尾。
            while (!File.Exists(outputComplete) && !connecting.IsCompleted)
                await Task.Delay(20, deadline.Token);
            var error = await Assert.ThrowsAsync<IOException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("exited with code 7", error.Message);
            Assert.Contains(header, error.Message);
            Assert.Contains("[diagnostics truncated]", error.Message);
            Assert.EndsWith(tail, error.Message);
            Assert.True(error.Message.Length <= ProcessDiagnostics.MaximumCharacters + 256);
            await capture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (inheritPipe)
            {
                using var descendant = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childId)));
                Assert.False(descendant.HasExited);
            }
        }
        finally
        {
            if (File.Exists(childId) && int.TryParse(await File.ReadAllTextAsync(childId), out int id))
            {
                try
                {
                    using var descendant = Process.GetProcessById(id);
                    descendant.Kill(entireProcessTree: true);
                    await descendant.WaitForExitAsync();
                }
                catch (ArgumentException) { }
            }
            directory.Delete(recursive: true);
        }
    }
}
