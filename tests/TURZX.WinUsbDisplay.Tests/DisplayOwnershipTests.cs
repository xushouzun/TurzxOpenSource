using System.ComponentModel;
using Xunit;

namespace TURZX.WinUsbDisplay.Tests;

/// <summary>验证设备接口独占所有权和错误分类。</summary>
public sealed class DisplayOwnershipTests
{
    /// <summary>验证不同接口可独立占用，关闭句柄后可重新取得所有权。</summary>
    [Fact]
    public void OwnershipIsPerDeviceAndReleasedWhenHandleCloses()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string first = Guid.NewGuid().ToString();
        string second = Guid.NewGuid().ToString();
        using (WinUsbTransport.AcquireOwnership(first))
        {
            var error = Assert.Throws<DisplayOpenException>(() => WinUsbTransport.AcquireOwnership(first.ToUpperInvariant()));
            Assert.Equal(DisplayOpenFailure.InUse, error.Failure);
            using var independent = WinUsbTransport.AcquireOwnership(second);
        }

        using var reopened = WinUsbTransport.AcquireOwnership(first);
    }

    /// <summary>验证访问拒绝映射为独立的异常分类。</summary>
    [Fact]
    public void AccessDeniedIsNotClassifiedAsOwnershipConflict()
    {
        var denied = Assert.IsType<DisplayOpenException>(DisplayOpenException.Classify(new Win32Exception(5)));
        Assert.Equal(DisplayOpenFailure.AccessDenied, denied.Failure);
        var disconnected = new Win32Exception(1167);
        Assert.Same(disconnected, DisplayOpenException.Classify(disconnected));
    }

    /// <summary>验证打开设备失败后会释放独占所有权。</summary>
    [Fact]
    public void FailedDeviceOpenReleasesOwnership()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string missingPath = Path.Combine(Path.GetTempPath(), $"winusb-missing-{Guid.NewGuid():N}");
        Assert.ThrowsAny<Win32Exception>(() =>
        {
            using var transport = new WinUsbTransport(missingPath);
        });

        using var reopened = WinUsbTransport.AcquireOwnership(missingPath);
    }
}
