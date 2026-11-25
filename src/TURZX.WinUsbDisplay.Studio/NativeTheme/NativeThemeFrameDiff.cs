using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TURZX.WinUsbDisplay.Studio.NativeTheme;

/// <summary>比较两帧画面并按 16 像素瓦片合并出变化区域。</summary>
internal static class NativeThemeFrameDiff
{
    internal const int TileSize = 16;

    /// <summary>返回需要发送的变化矩形；尺寸不一致时返回整帧。</summary>
    internal static IReadOnlyList<Rectangle> FindRegions(Bitmap previous, Bitmap current)
    {
        int width = current.Width;
        int height = current.Height;
        if (previous.Width != width || previous.Height != height)
            return [new Rectangle(0, 0, width, height)];

        byte[] before = GetPixels(previous);
        byte[] after = GetPixels(current);
        int tilesX = (width + TileSize - 1) / TileSize;
        int tilesY = (height + TileSize - 1) / TileSize;
        var changed = new bool[tilesX, tilesY];
        for (int tileY = 0; tileY < tilesY; tileY++)
        {
            int y0 = tileY * TileSize;
            int y1 = Math.Min(y0 + TileSize, height);
            for (int tileX = 0; tileX < tilesX; tileX++)
            {
                int x0 = tileX * TileSize;
                int x1 = Math.Min(x0 + TileSize, width);
                bool dirty = false;
                for (int y = y0; y < y1 && !dirty; y++)
                {
                    int offset = (y * width + x0) * 4;
                    for (int x = x0; x < x1; x++, offset += 4)
                    {
                        if (before[offset] == after[offset]
                            && before[offset + 1] == after[offset + 1]
                            && before[offset + 2] == after[offset + 2]
                            && before[offset + 3] == after[offset + 3])
                            continue;
                        dirty = true;
                        break;
                    }
                }
                changed[tileX, tileY] = dirty;
            }
        }

        var regions = new List<Rectangle>();
        for (int tileY = 0; tileY < tilesY; tileY++)
        {
            int y = tileY * TileSize;
            int heightInTile = Math.Min(TileSize, height - y);
            int tileX = 0;
            while (tileX < tilesX)
            {
                if (!changed[tileX, tileY])
                {
                    tileX++;
                    continue;
                }
                int start = tileX;
                while (tileX < tilesX && changed[tileX, tileY])
                    tileX++;
                int x = start * TileSize;
                int widthInTile = Math.Min(tileX * TileSize, width) - x;
                bool merged = false;
                for (int index = regions.Count - 1; index >= 0; index--)
                {
                    Rectangle existing = regions[index];
                    if (existing.X == x && existing.Width == widthInTile && existing.Bottom == y)
                    {
                        regions[index] = Rectangle.FromLTRB(existing.Left, existing.Top, existing.Right,
                            y + heightInTile);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    regions.Add(new Rectangle(x, y, widthInTile, heightInTile));
            }
        }
        return regions;
    }

    private static byte[] GetPixels(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        var result = new byte[checked(width * height * 4)];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var row = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                Buffer.BlockCopy(row, 0, result, y * width * 4, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return result;
    }
}
