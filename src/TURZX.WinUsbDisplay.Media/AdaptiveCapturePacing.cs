using System.Diagnostics;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>根据捕捉和发送耗时调整取帧间隔。负载上升时可立即降速，恢复时逐步提高帧率。</summary>
/// <param name="ceilingFps">取帧帧率的上限。</param>
internal sealed class AdaptiveCapturePacing(int ceilingFps)
{
    /// <summary>每秒恢复的帧率占上限帧率的比例。</summary>
    private const double CeilingFractionPerSecond = 0.125;
    /// <summary>当前自适应目标帧率。</summary>
    private double targetFps = Math.Max(1, ceilingFps / 2d);
    /// <summary>上一次更新目标帧率时的 Stopwatch 时间戳。</summary>
    private long lastAdjustment;
    /// <summary>平滑后的捕捉耗时，单位为毫秒。</summary>
    private double captureMs;
    /// <summary>平滑后的发送耗时，单位为毫秒，由发送线程更新。</summary>
    private double sendMs;

    /// <summary>按当前处理能力和恢复速率计算下一次取帧间隔。</summary>
    internal TimeSpan GetInterval()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = lastAdjustment == 0 ? 0 : Stopwatch.GetElapsedTime(lastAdjustment, now).TotalSeconds;
        lastAdjustment = now;

        // 捕捉和发送可并行，持续帧率由较慢的一端决定，并预留 5% 的耗时余量。
        double workMs = Math.Max(captureMs, Volatile.Read(ref sendMs)) * 1.05;
        double sustainableFps = workMs > 0 ? Math.Min(ceilingFps, 1000d / workMs) : ceilingFps;
        double increase = elapsedSeconds * ceilingFps * CeilingFractionPerSecond;
        targetFps = Math.Min(sustainableFps, targetFps + increase);
        return TimeSpan.FromSeconds(1d / targetFps);
    }

    /// <summary>记录一次捕捉的耗时。</summary>
    internal void ObserveCapture(TimeSpan elapsed) => captureMs = Smooth(captureMs, elapsed.TotalMilliseconds);

    // 发送线程写入耗时，取帧线程读取最近的平滑值。
    /// <summary>从发送线程更新 USB 发送耗时。</summary>
    internal void ObserveSend(TimeSpan elapsed) =>
        Volatile.Write(ref sendMs, Smooth(sendMs, elapsed.TotalMilliseconds));

    /// <summary>对耗时样本做平滑处理，对耗时上升反应更快。</summary>
    private static double Smooth(double previous, double sample) =>
        previous == 0 ? sample : previous + (sample - previous) * (sample > previous ? .35 : .12);
}
