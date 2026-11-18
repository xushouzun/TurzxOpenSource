namespace TURZX.WinUsbDisplay.Windows.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("WinUsbDisplay-WindowsTests-").FullName;

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
