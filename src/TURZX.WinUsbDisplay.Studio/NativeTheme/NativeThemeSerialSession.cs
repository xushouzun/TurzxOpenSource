using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Ports;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>通过 CH340 串口向 3.5 寸原生主题屏发送 RGB565 图像。</summary>
internal sealed class NativeThemeSerialSession : IDisposable
{
    private readonly SerialPort port;

    /// <summary>打开指定 COM 口。</summary>
    internal NativeThemeSerialSession(string portName)
    {
        port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            DtrEnable = true,
            RtsEnable = true,
            Handshake = Handshake.None,
            ReadTimeout = 1000,
            WriteTimeout = 60000
        };
        port.Open();
    }

    /// <summary>唤醒屏幕并设置亮度。</summary>
    internal void Wake(byte brightness = 255)
    {
        SendRegister(110, 0, 0, 0, 0);
        SendRegister(109, 0, 0, 0, 0);
        SendRegister(110, brightness, 0, 0, 0);
    }

    /// <summary>发送完整 RGB565 画面。</summary>
    internal void SendFrame(Bitmap bitmap)
    {
        byte[] pixels = ToRgb565(bitmap);
        SendRegister(197, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
        Thread.Sleep(10);
        port.Write(pixels, 0, pixels.Length);
        Thread.Sleep(10);
    }

    private void SendRegister(int register, int x, int y, int endX, int endY, byte[]? payload = null)
    {
        var header = new byte[6];
        header[0] = (byte)(x >> 2);
        header[1] = (byte)(((x & 0x03) << 6) + (y >> 4));
        header[2] = (byte)(((y & 0x0f) << 4) + (endX >> 6));
        header[3] = (byte)(((endX & 0x3f) << 2) + (endY >> 8));
        header[4] = (byte)(endY & 0xff);
        header[5] = (byte)register;
        port.Write(header, 0, header.Length);
        if (payload is not null)
            port.Write(payload, 0, payload.Length);
    }

    private static byte[] ToRgb565(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var result = new byte[checked(width * height * 2)];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            int target = 0;
            for (int y = 0; y < height; y++)
            {
                IntPtr source = data.Scan0 + y * data.Stride;
                System.Runtime.InteropServices.Marshal.Copy(source, row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    int offset = x * 4;
                    byte blue = row[offset];
                    byte green = row[offset + 1];
                    byte red = row[offset + 2];
                    ushort value = (ushort)(((red >> 3) << 11) | ((green >> 2) << 5) | (blue >> 3));
                    result[target++] = (byte)(value & 0xff);
                    result[target++] = (byte)(value >> 8);
                }
            }
            return result;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose() => port.Dispose();
}
