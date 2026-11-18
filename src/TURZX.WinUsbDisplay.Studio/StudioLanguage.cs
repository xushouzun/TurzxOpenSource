using System.Globalization;
using System.Text.Json;
using System.Windows;

namespace TURZX.WinUsbDisplay.Studio;

/// <summary>加载 Studio 翻译目录，并按当前语言解析界面资源。</summary>
internal static class StudioLanguage
{
    /// <summary>未指定语言时使用的语言代码。</summary>
    internal const string DefaultLanguage = "zh-CN";
    /// <summary>英语翻译目录的语言代码。</summary>
    internal const string EnglishLanguage = "en-US";

    /// <summary>表示语言选择器中的语言选项。</summary>
    /// <param name="Code">区域性及翻译目录所用的语言代码。</param>
    /// <param name="DisplayName">语言选择器中显示的名称。</param>
    /// <param name="DocumentationSuffix">本地化文档文件名的后缀。</param>
    internal sealed record Option(string Code, string DisplayName, string DocumentationSuffix)
    {
        /// <summary>获取对应语言的区域性设置。</summary>
        internal CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(Code);
    }

    /// <summary>列出 Studio 当前提供的界面语言。</summary>
    internal static IReadOnlyList<Option> Options { get; } = Array.AsReadOnly<Option>(
    [
        new(DefaultLanguage, "简体中文", ""),
        new(EnglishLanguage, "English", ".en"),
        new("ja-JP", "日本語", ".ja"),
        new("ko-KR", "한국어", ".ko")
    ]);

    /// <summary>获取默认语言选项。</summary>
    private static readonly Option DefaultOption = Options[0];
    /// <summary>按语言缓存翻译目录，简体中文与其他语言使用同一套英文键。</summary>
    private static readonly IReadOnlyDictionary<string, Lazy<IReadOnlyDictionary<string, string>>> Catalogs =
        Options.ToDictionary(option => option.Code,
            option => new Lazy<IReadOnlyDictionary<string, string>>(() => LoadCatalog(option.Code)),
            StringComparer.Ordinal);
    /// <summary>当前选中的界面语言。</summary>
    private static Option current = DefaultOption;
    /// <summary>供 WPF 动态资源绑定使用的本地化字典。</summary>
    private static ResourceDictionary? resources;

    /// <summary>获取当前界面语言。</summary>
    internal static string Current => Volatile.Read(ref current).Code;
    /// <summary>返回当前语言对应的内置文档文件名后缀。</summary>
    internal static string DocumentationSuffix => Volatile.Read(ref current).DocumentationSuffix;

    /// <summary>为翻译键添加 WPF 动态资源前缀。</summary>
    internal static string ResourceKey(string key) => "L." + key;

    /// <summary>切换界面翻译并刷新资源。</summary>
    internal static void Set(string? language)
    {
        Option option = Options.FirstOrDefault(entry => string.Equals(entry.Code, language, StringComparison.OrdinalIgnoreCase))
            ?? DefaultOption;
        Volatile.Write(ref current, option);
        if (Application.Current is not { } app)
            return;

        var dictionary = new ResourceDictionary();
        foreach (string key in Catalogs[DefaultLanguage].Value.Keys)
            dictionary[ResourceKey(key)] = Resolve(key, option);

        int index = resources is null ? -1 : app.Resources.MergedDictionaries.IndexOf(resources);
        if (index < 0)
            app.Resources.MergedDictionaries.Add(dictionary);
        else
            app.Resources.MergedDictionaries[index] = dictionary;
        resources = dictionary;
    }

    /// <summary>按英文键查找当前语言文案，并应用可选的格式参数。</summary>
    internal static string T(string key, params object[] arguments)
    {
        Option option = Volatile.Read(ref current);
        string value = Resolve(key, option);
        return arguments.Length == 0 ? value : string.Format(option.Culture, value, arguments);
    }

    /// <summary>关联界面翻译键，保留原异常类型和诊断消息。</summary>
    internal static TException UiError<TException>(TException error, string key) where TException : Exception =>
        LocalizableError.Mark(error, key);

    /// <summary>获取异常关联的界面翻译键。</summary>
    internal static string? GetUiErrorKey(Exception error) => LocalizableError.GetKey(error);

    /// <summary>依次从当前语言、英语和默认语言查找文案。</summary>
    private static string Resolve(string key, Option option)
    {
        if (Catalogs[option.Code].Value.TryGetValue(key, out string? translation))
            return translation;
        if (option.Code != EnglishLanguage && Catalogs[EnglishLanguage].Value.TryGetValue(key, out translation))
            return translation;
        return Catalogs[DefaultLanguage].Value.TryGetValue(key, out translation) ? translation : key;
    }

    /// <summary>从随应用安装的 JSON 文件载入指定语言目录。</summary>
    private static IReadOnlyDictionary<string, string> LoadCatalog(string code)
    {
        using var input = typeof(StudioLanguage).Assembly.GetManifestResourceStream($"Studio.Localization.{code}.json")
            ?? throw new InvalidOperationException($"Studio localization catalog '{code}' was not found.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(input)
            ?? throw new InvalidOperationException($"Studio localization catalog '{code}' was empty.");
    }
}
