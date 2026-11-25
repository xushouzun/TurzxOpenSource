namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>
/// 演示用数据源：返回随时间轻微变化的模拟数值，仅用于预览展示，不代表真实硬件。
/// 接入真实数据时请实现 <see cref="INativeThemeDataSource"/> 并替换
/// <see cref="NativeThemeDataSources.Current"/>。
/// </summary>
internal sealed class NativeThemeMockDataSource : INativeThemeDataSource
{
    internal static readonly NativeThemeMockDataSource Instance = new();

    public NativeThemeData? Read()
    {
        DateTime now = DateTime.Now;
        double seconds = now.TimeOfDay.TotalSeconds;
        double fast = Math.Sin(seconds / 7);
        double slow = Math.Sin(seconds / 19);
        return new()
        {
            CpuLoadPercent = 32 + 8 * fast,
            CpuTemperatureCelsius = 56 + 2 * slow,
            CpuFrequencyMhz = 4600 + 200 * fast,
            CpuFanRpm = 1250 + 120 * fast,
            CpuPowerWatts = 65 + 6 * fast,
            GpuLoadPercent = 41 + 6 * Math.Sin(seconds / 9 + 1),
            GpuTemperatureCelsius = 62 + 2 * slow,
            GpuFanRpm = 1800 + 150 * fast,
            GpuMemoryUsedMb = 8100 + 300 * slow,
            GpuMemoryLoadPercent = 38 + 3 * fast,
            RamLoadPercent = 58 + 2 * slow,
            RamUsedMb = 18600 + 200 * slow,
            RamTemperatureCelsius = 45 + slow,
            DiskLoadPercent = 35 + 3 * fast,
            DiskTemperatureCelsius = 39 + slow,
            VolumePercent = 26,
            NetworkUploadMbps = Math.Max(0, 1.2 + 0.8 * Math.Sin(seconds / 3)),
            NetworkDownloadMbps = Math.Max(0, 8.4 + 2.5 * Math.Sin(seconds / 4 + 0.5)),
            Time = now
        };
    }
}
