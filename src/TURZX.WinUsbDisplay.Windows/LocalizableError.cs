namespace TURZX.WinUsbDisplay.Windows;

/// <summary>为异常关联界面翻译键，供显示时按当前语言解析。</summary>
internal static class LocalizableError
{
    /// <summary>用于存取界面翻译键的异常数据索引。</summary>
    private static readonly object TranslationKey = new();

    /// <summary>将界面翻译键附加到异常。</summary>
    internal static TException Mark<TException>(TException error, string key) where TException : Exception
    {
        error.Data[TranslationKey] = key;
        return error;
    }

    /// <summary>读取此前保存的界面翻译键。</summary>
    internal static string? GetKey(Exception error) => error.Data[TranslationKey] as string;
}
