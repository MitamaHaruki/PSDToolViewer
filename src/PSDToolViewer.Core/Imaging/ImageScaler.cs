namespace PSDToolViewer.Core.Imaging;

/// <summary>画像の縮小（面積平均）。</summary>
public static class ImageScaler
{
    /// <summary>
    /// 縦横の長い方が <paramref name="maxSize"/> 以下になるよう縮小する（拡大はしない）。
    /// 透明部分の色がにじまないよう、不透明度で重み付けして平均する。
    /// </summary>
    public static PixelImage Fit(PixelImage image, int maxSize)
    {
        if (image.IsEmpty || (image.Width <= maxSize && image.Height <= maxSize)) return image;
        double scale = Math.Min((double)maxSize / image.Width, (double)maxSize / image.Height);
        int tw = Math.Max(1, (int)Math.Round(image.Width * scale));
        int th = Math.Max(1, (int)Math.Round(image.Height * scale));
        var src = image.Data;
        int sw = image.Width, sh = image.Height;
        var dst = new byte[tw * th * 4];

        Parallel.For(0, th, ty =>
        {
            int y0 = ty * sh / th, y1 = Math.Max(y0 + 1, (ty + 1) * sh / th);
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = tx * sw / tw, x1 = Math.Max(x0 + 1, (tx + 1) * sw / tw);
                long b = 0, g = 0, r = 0, a = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * sw * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x * 4;
                        int alpha = src[i + 3];
                        b += src[i] * alpha;
                        g += src[i + 1] * alpha;
                        r += src[i + 2] * alpha;
                        a += alpha;
                    }
                }
                int o = (ty * tw + tx) * 4;
                int count = (y1 - y0) * (x1 - x0);
                if (a > 0)
                {
                    dst[o] = (byte)Math.Min(255, b / a);
                    dst[o + 1] = (byte)Math.Min(255, g / a);
                    dst[o + 2] = (byte)Math.Min(255, r / a);
                }
                dst[o + 3] = (byte)(a / count);
            }
        });
        return new PixelImage(tw, th, dst);
    }
}
