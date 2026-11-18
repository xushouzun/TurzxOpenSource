using System.Text;

namespace TURZX.WinUsbDisplay.Media;

/// <summary>持续排空进程诊断管道，仅保留有界的头部信息和尾部错误。</summary>
internal sealed class ProcessDiagnostics
{
    internal const int MaximumCharacters = 64 * 1024;
    private const int HalfLimit = MaximumCharacters / 2;
    private const string Omitted = "\n[diagnostics truncated]\n";
    private readonly Lock gate = new();
    private readonly StringBuilder head = new();
    private readonly StringBuilder tail = new();
    private bool truncated;

    internal ProcessDiagnostics(StreamReader reader) => Completion = ReadAsync(reader);

    internal Task Completion { get; }

    internal string Text
    {
        get
        {
            lock (gate)
                return head.ToString() + (truncated ? Omitted : "") + tail.ToString();
        }
    }

    private async Task ReadAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            lock (gate)
            {
                int prefix = Math.Min(count, HalfLimit - head.Length);
                head.Append(buffer, 0, prefix);
                tail.Append(buffer, prefix, count - prefix);
                if (tail.Length > HalfLimit)
                {
                    tail.Remove(0, tail.Length - HalfLimit);
                    truncated = true;
                }
            }
        }
    }
}
