using PSDToolViewer.Core.Imaging;

namespace PSDToolViewer.Core.Tests;

internal static class TestImages
{
    /// <summary>単色の画像（非乗算済み BGRA）。</summary>
    public static PixelImage Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            data[i * 4] = b;
            data[i * 4 + 1] = g;
            data[i * 4 + 2] = r;
            data[i * 4 + 3] = a;
        }
        return new PixelImage(width, height, data);
    }

    /// <summary>位置によって色が変わる画像。</summary>
    public static PixelImage Pattern(int width, int height, int seed)
    {
        var random = new Random(seed);
        var data = new byte[width * height * 4];
        random.NextBytes(data);
        // 圧縮が効く部分も作る
        for (int i = 0; i < data.Length / 3; i++) data[i] = (byte)(i / 64);
        return new PixelImage(width, height, data);
    }

    public static (byte B, byte G, byte R, byte A) Pixel(PixelImage image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return (image.Data[i], image.Data[i + 1], image.Data[i + 2], image.Data[i + 3]);
    }
}
