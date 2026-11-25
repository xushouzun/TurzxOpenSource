using System.Windows;
using System.Windows.Controls;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>提供窗口标题栏的最小化、最大化和关闭控件。</summary>
public partial class CaptionButtons : UserControl
{
    /// <summary>初始化标题栏按钮控件。</summary>
    public CaptionButtons() => InitializeComponent();

    /// <summary>将所属窗口最小化。</summary>
    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(Window.GetWindow(this));

    /// <summary>在最大化和普通窗口状态之间切换。</summary>
    private void OnToggleMaximize(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window.WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(window);
        else
            SystemCommands.MaximizeWindow(window);
    }

    /// <summary>关闭控件所在窗口。</summary>
    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(Window.GetWindow(this));
}
