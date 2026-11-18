using System.ComponentModel;

namespace TURZX.WinUsbDisplay;

/// <summary>打开显示设备时发生的 Windows 共享或访问错误。</summary>
public enum DisplayOpenFailure
{
    /// <summary>显示设备已被其他会话占用，或 Windows 报告共享冲突。</summary>
    InUse,
    /// <summary>Windows 拒绝访问设备接口。</summary>
    AccessDenied
}

/// <summary>报告打开显示设备会话失败的具体原因。</summary>
public sealed class DisplayOpenException : Win32Exception
{
    /// <summary>获取无法打开显示设备的原因。</summary>
    public DisplayOpenFailure Failure { get; }

    /// <summary>将 Windows 打开错误与 SDK 的错误分类关联起来。</summary>
    private DisplayOpenException(Win32Exception error, DisplayOpenFailure failure)
        : base(error.NativeErrorCode, error.Message) => Failure = failure;

    /// <summary>将共享冲突和访问拒绝转换为可供调用方识别的异常。</summary>
    internal static Win32Exception Classify(Win32Exception error) => error.NativeErrorCode switch
    {
        32 => new DisplayOpenException(error, DisplayOpenFailure.InUse),
        5 => new DisplayOpenException(error, DisplayOpenFailure.AccessDenied),
        _ => error
    };
}
