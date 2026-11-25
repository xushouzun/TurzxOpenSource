using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>随 Studio 分发的桌面模式显示驱动，负责检测状态并提权安装或卸载。</summary>
internal static class DesktopModeDriver
{
    internal const string InfName = "TURZX_display_driver.inf";

    /// <summary>随程序复制的驱动目录。</summary>
    internal static string DriverDirectory => Path.Combine(AppContext.BaseDirectory, "Driver");

    /// <summary>驱动 INF 文件路径。</summary>
    internal static string InfPath => Path.Combine(DriverDirectory, InfName);

    /// <summary>当前构建是否包含桌面模式驱动。</summary>
    internal static bool IsAvailable => File.Exists(InfPath);

    /// <summary>系统是否已安装桌面模式驱动。</summary>
    internal static bool IsInstalled => IsInstalledIn(Environment.GetFolderPath(Environment.SpecialFolder.System));

    /// <summary>
    /// 检查指定 Windows 系统目录下是否存在已安装的驱动包。
    /// 只认 DriverStore：pnputil 卸载后 INF 复制到 drivers\UMDF 的 DLL 会残留，不能作为已安装依据。
    /// </summary>
    internal static bool IsInstalledIn(string systemDirectory)
    {
        if (string.IsNullOrEmpty(systemDirectory))
            return false;

        string repository = Path.Combine(systemDirectory, "DriverStore", "FileRepository");
        return Directory.Exists(repository)
            && Directory.EnumerateDirectories(repository, "turzx_display_driver.inf_*").Any();
    }

    /// <summary>通过系统 pnputil 提权安装驱动；用户取消 UAC 时抛出错误码 1223 的异常。</summary>
    internal static async Task<bool> InstallAsync(CancellationToken cancellation = default)
    {
        if (IsInstalled)
            return true;
        if (!IsAvailable)
            return false;

        int exitCode = await RunElevatedAsync($"/add-driver \"{InfPath}\" /install", cancellation).ConfigureAwait(false);
        return exitCode is 0 or 3010;
    }

    /// <summary>通过系统 pnputil 提权卸载驱动；先删除设备绑定，再移除驱动包。</summary>
    internal static async Task<bool> UninstallAsync(CancellationToken cancellation = default)
    {
        var packages = await FindPublishedNamesAsync(cancellation).ConfigureAwait(false);
        if (packages.Count == 0)
            return !IsInstalled;

        foreach (string package in packages)
        {
            int exitCode = await RunElevatedAsync($"/delete-driver {package} /uninstall /force", cancellation).ConfigureAwait(false);
            if (exitCode is not (0 or 3010))
                return false;
        }
        return true;
    }

    /// <summary>从 pnputil /enum-drivers 输出中提取本驱动的已发布包名。</summary>
    internal static IReadOnlyList<string> ParsePublishedNames(string output)
    {
        var names = new List<string>();
        foreach (string block in output.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.Contains(InfName, StringComparison.OrdinalIgnoreCase))
                continue;
            Match match = Regex.Match(block, @"\boem\d+\.inf\b", RegexOptions.IgnoreCase);
            if (match.Success)
                names.Add(match.Value);
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<IReadOnlyList<string>> FindPublishedNamesAsync(CancellationToken cancellation)
    {
        var startInfo = new ProcessStartInfo(PnPUtilPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/enum-drivers");
        using Process? process = Process.Start(startInfo);
        if (process is null)
            return [];
        string output = await process.StandardOutput.ReadToEndAsync(cancellation).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return ParsePublishedNames(output);
    }

    private static async Task<int> RunElevatedAsync(string arguments, CancellationToken cancellation)
    {
        var startInfo = new ProcessStartInfo(PnPUtilPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = arguments,
            WindowStyle = ProcessWindowStyle.Normal
        };
        using Process? process = Process.Start(startInfo);
        if (process is null)
            return -1;
        await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static string PnPUtilPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
}
