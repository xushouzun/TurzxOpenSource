namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>
/// 原生主题可消费的一次传感器快照。未提供的指标保持为 <see langword="null"/>，
/// 渲染器会回退到主题文件中的存档值。项目本身不采集任何实时硬件数据。
/// </summary>
public sealed record NativeThemeData
{
    /// <summary>CPU 总占用率（0–100）。</summary>
    public double? CpuLoadPercent { get; init; }

    /// <summary>CPU 温度（摄氏度）。</summary>
    public double? CpuTemperatureCelsius { get; init; }

    /// <summary>CPU 当前频率（MHz）。</summary>
    public double? CpuFrequencyMhz { get; init; }

    /// <summary>CPU 风扇转速（RPM）。</summary>
    public double? CpuFanRpm { get; init; }

    /// <summary>CPU 功耗（W）。</summary>
    public double? CpuPowerWatts { get; init; }

    /// <summary>GPU 总占用率（0–100）。</summary>
    public double? GpuLoadPercent { get; init; }

    /// <summary>GPU 温度（摄氏度）。</summary>
    public double? GpuTemperatureCelsius { get; init; }

    /// <summary>GPU 风扇转速（RPM）。</summary>
    public double? GpuFanRpm { get; init; }

    /// <summary>GPU 显存已用量（MB）。</summary>
    public double? GpuMemoryUsedMb { get; init; }

    /// <summary>GPU 显存占用率（0–100）。</summary>
    public double? GpuMemoryLoadPercent { get; init; }

    /// <summary>内存占用率（0–100）。</summary>
    public double? RamLoadPercent { get; init; }

    /// <summary>内存已用量（MB）。</summary>
    public double? RamUsedMb { get; init; }

    /// <summary>内存温度（摄氏度）。</summary>
    public double? RamTemperatureCelsius { get; init; }

    /// <summary>系统盘占用率（0–100）。</summary>
    public double? DiskLoadPercent { get; init; }

    /// <summary>系统盘温度（摄氏度）。</summary>
    public double? DiskTemperatureCelsius { get; init; }

    /// <summary>系统音量（0–100）。</summary>
    public double? VolumePercent { get; init; }

    /// <summary>网络上传速率（MB/s）。</summary>
    public double? NetworkUploadMbps { get; init; }

    /// <summary>网络下载速率（MB/s）。</summary>
    public double? NetworkDownloadMbps { get; init; }

    /// <summary>时间；用于 Time 和 Date 条目。</summary>
    public DateTime? Time { get; init; }

    /// <summary>天气长文本。</summary>
    public string? Weather { get; init; }

    /// <summary>型号等自定义长文本。</summary>
    public string? Model { get; init; }
}

/// <summary>
/// 原生主题数据源扩展点。实现该接口即可提供 CPU、GPU、内存、磁盘、网络等指标；
/// 返回 <see langword="null"/> 或指标为 null 时沿用主题文件中的存档值或示例占位。
/// </summary>
public interface INativeThemeDataSource
{
    /// <summary>读取一次数据快照；返回 null 表示全部使用主题存档值。</summary>
    NativeThemeData? Read();
}

/// <summary>原生主题数据源注册点。默认使用随时间变化的模拟数据源。</summary>
public static class NativeThemeDataSources
{
    private static INativeThemeDataSource? current = NativeThemeMockDataSource.Instance;

    /// <summary>
    /// 当前数据源。默认返回模拟演示数值；设置为自定义实现可接入真实数据，
    /// 设置为 null 时回退到主题存档值和示例占位值。
    /// </summary>
    public static INativeThemeDataSource? Current
    {
        get => Volatile.Read(ref current);
        set => Volatile.Write(ref current, value);
    }
}
