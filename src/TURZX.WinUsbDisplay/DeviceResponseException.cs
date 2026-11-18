namespace TURZX.WinUsbDisplay;

/// <summary>对无法取得有效 Win207 应答的情况分类。</summary>
public enum DeviceResponseFailure
{
    /// <summary>读取完成或超时时，输入端点没有返回数据。</summary>
    NoData,

    /// <summary>应答与已验证的查询格式不符。</summary>
    UnexpectedReply,

    /// <summary>固件报告无法创建请求的目录。</summary>
    DirectoryCreationFailed
}

/// <summary>表示请求未收到预期应答。</summary>
public sealed class DeviceResponseException : IOException
{
    /// <summary>记录应答故障类型、原始字节及底层异常。</summary>
    internal DeviceResponseException(DeviceResponseFailure failure, string message, Exception? innerException = null,
        ReadOnlyMemory<byte> response = default)
        : base(message, innerException)
    {
        Failure = failure;
        Response = response;
    }

    /// <summary>获取最近一次收到的应答字节。没有应答时为空。</summary>
    public ReadOnlyMemory<byte> Response { get; }

    /// <summary>获取可供程序判断的应答失败类型。</summary>
    public DeviceResponseFailure Failure { get; }
}
