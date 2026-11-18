using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>管理 Studio 的应用启动流程。</summary>
public partial class App : System.Windows.Application
{
    /// <summary>读取启动设置并显示主窗口。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 某些 GPU 驱动会导致 WPF 窗口黑屏。
        // 在当前进程内强制使用软件渲染，效果等同于设置注册表项 DisableHWAcceleration，
        // 同时保留原有系统设置。
        string? mode = Environment.GetEnvironmentVariable("WINUSB_DISPLAY_SOFTWARE_RENDER");
        if (mode is "1" || string.Equals(mode, "true", StringComparison.OrdinalIgnoreCase))
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
    }
}