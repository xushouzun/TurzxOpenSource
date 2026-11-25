using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Ports;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>通过 CH340 串口向 3.5 寸原生主题屏发送 RGB565 图像。</summary>
internal sealed class NativeThemeSerialSession : IDisposable
{
    private readonly SerialPort port;
    private readonly object writeGate = new();

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

    /// <summary>发送连接探测命令，原项目在开始播放前会先发送该命令。</summary>
    internal void TestConnection() => SendRegister(255, 0, 0, 0, 0);

    /// <summary>唤醒屏幕，不改变亮度。</summary>
    internal void Wake()
    {
        SendRegister(110, 0, 0, 0, 0);
        SendRegister(109, 0, 0, 0, 0);
    }

    /// <summary>设置镜像模式，0 为正常显示。</summary>
    internal void SetMirrorMode(int mode)
    {
        var data = new byte[16];
        data[6] = (byte)mode;
        SendRegister(122, 0, 0, 0, 0, data);
    }

    /// <summary>设置固件旋转方向，并告知输出宽高。</summary>
    internal void SetRotation(int type, int width, int height)
    {
        if ((uint)type > 3)
            throw new ArgumentOutOfRangeException(nameof(type));
        var data = new byte[16];
        data[6] = (byte)(type + 100);
        data[7] = (byte)(width >> 8);
        data[8] = (byte)width;
        data[9] = (byte)(height >> 8);
        data[10] = (byte)height;
        SendRegister(121, 0, 0, 0, 0, data);
    }

    /// <summary>设置背光亮度，取值 0–255。</summary>
    /// <summary>设置背光亮度。寄存器 110 的值越大越暗，这里把亮度刻度换算为反相的寄存器值。</summary>
    internal void SetBrightness(byte brightness) => SendRegister(110, BrightnessRegisterValue(brightness), 0, 0, 0);

    /// <summary>把 0（最暗）到 255（最亮）的亮度刻度换算为寄存器 110 的暗度值。</summary>
    internal static byte BrightnessRegisterValue(byte brightness) => (byte)(255 - brightness);

    /// <summary>发送完整 RGB565 画面。</summary>
    internal void SendFrame(Bitmap bitmap) =>
        SendRegion(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

    /// <summary>只发送指定矩形区域的 RGB565 像素，用于实时刷新。</summary>
    internal void SendRegion(Bitmap bitmap, Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return;
        byte[] pixels = ToRgb565(bitmap, region);
        byte[] header = BuildRegisterPacket(197, region.X, region.Y, region.Right - 1, region.Bottom - 1, null);
        lock (writeGate)
        {
            port.Write(header, 0, header.Length);
            Thread.Sleep(10);
            port.Write(pixels, 0, pixels.Length);
            Thread.Sleep(10);
        }
    }

    private void SendRegister(int register, int x, int y, int endX, int endY, byte[]? payload = null)
    {
        byte[] packet = BuildRegisterPacket(register, x, y, endX, endY, payload);
        lock (writeGate)
            port.Write(packet, 0, packet.Length);
    }

    /// <summary>
    /// 按原项目把寄存器头和载荷合并成一次写入。载荷前 6 字节是头部占位，数据从索引 6 开始，
    /// 例如镜像是固定 16 字节包；拆成 6+16 字节会让固件多读 6 字节并让后续亮度/图像错位。
    /// </summary>
    internal static byte[] BuildRegisterPacket(int register, int x, int y, int endX, int endY, byte[]? payload)
    {
        byte[] header = BuildRegisterHeader(register, x, y, endX, endY);
        if (payload is null || payload.Length <= header.Length)
            return header;

        var packet = new byte[payload.Length];
        Array.Copy(header, packet, header.Length);
        Array.Copy(payload, header.Length, packet, header.Length, payload.Length - header.Length);
        return packet;
    }

    /// <summary>按原项目 10 位坐标格式生成寄存器头。</summary>
    internal static byte[] BuildRegisterHeader(int register, int x, int y, int endX, int endY)
    {
        var header = new byte[6];
        header[0] = (byte)(x >> 2);
        header[1] = (byte)(((x & 0x03) << 6) + (y >> 4));
        header[2] = (byte)(((y & 0x0f) << 4) + (endX >> 6));
        header[3] = (byte)(((endX & 0x3f) << 2) + (endY >> 8));
        header[4] = (byte)(endY & 0xff);
        header[5] = (byte)register;
        return header;
    }

    internal static byte[] ToRgb565(Bitmap bitmap) =>
        ToRgb565(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

    internal static byte[] ToRgb565(Bitmap bitmap, Rectangle region)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var result = new byte[checked(region.Width * region.Height * 2)];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            int target = 0;
            for (int y = 0; y < region.Height; y++)
            {
                IntPtr source = data.Scan0 + (region.Y + y) * data.Stride;
                System.Runtime.InteropServices.Marshal.Copy(source, row, 0, row.Length);
                int offset = region.X * 4;
                for (int x = 0; x < region.Width; x++, offset += 4)
                {
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
