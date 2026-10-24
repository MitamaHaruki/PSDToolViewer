using PSDToolViewer.Core.Imaging;

namespace PSDToolViewer.Core.Rendering;

/// <summary>合成用の作業領域。乗算済み BGRA (Pbgra32)。</summary>
public sealed class Canvas
{
    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new byte[checked(width * height * 4)];
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;

    /// <summary>乗算済み BGRA の画素データ。</summary>
    public byte[] Data { get; }

    public PixelRect Rect => new(0, 0, Width, Height);

    /// <summary>描画済みの可能性がある範囲。これ以外の画素はすべて 0。</summary>
    public PixelRect Bounds { get; internal set; } = PixelRect.Empty;

    public void Clear()
    {
        var b = Bounds;
        if (b.IsEmpty) return;
        for (int y = b.Y; y < b.Bottom; y++)
            Array.Clear(Data, (y * Width + b.X) * 4, b.Width * 4);
        Bounds = PixelRect.Empty;
    }

    public void CopyFrom(Canvas source)
    {
        if (source.Width != Width || source.Height != Height) throw new ArgumentException("サイズが一致しません。");
        Clear();
        var b = source.Bounds;
        for (int y = b.Y; y < b.Bottom; y++)
            Buffer.BlockCopy(source.Data, (y * Width + b.X) * 4, Data, (y * Width + b.X) * 4, b.Width * 4);
        Bounds = b;
    }

    /// <summary>不透明な画素がある最小範囲。</summary>
    public PixelRect ComputeOpaqueBounds()
    {
        var b = Bounds;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = b.Y; y < b.Bottom; y++)
        {
            int row = y * Width * 4;
            for (int x = b.X; x < b.Right; x++)
            {
                if (Data[row + x * 4 + 3] == 0) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                bottom = y;
            }
        }
        return right < 0 ? PixelRect.Empty : PixelRect.FromEdges(left, top, right + 1, bottom + 1);
    }

    /// <summary>指定範囲を非乗算済みの画像として取り出す。</summary>
    public PixelImage ToStraightImage(PixelRect? region = null)
    {
        var r = (region ?? Rect).Intersect(Rect);
        if (r.IsEmpty) return PixelImage.Empty;
        var dst = new byte[r.Width * r.Height * 4];
        Parallel.For(0, r.Height, y =>
        {
            int si = ((r.Y + y) * Width + r.X) * 4;
            int di = y * r.Width * 4;
            for (int x = 0; x < r.Width; x++, si += 4, di += 4)
            {
                int a = Data[si + 3];
                if (a == 0) continue;
                if (a == 255)
                {
                    dst[di] = Data[si];
                    dst[di + 1] = Data[si + 1];
                    dst[di + 2] = Data[si + 2];
                }
                else
                {
                    dst[di] = (byte)Math.Min(255, (Data[si] * 255 + a / 2) / a);
                    dst[di + 1] = (byte)Math.Min(255, (Data[si + 1] * 255 + a / 2) / a);
                    dst[di + 2] = (byte)Math.Min(255, (Data[si + 2] * 255 + a / 2) / a);
                }
                dst[di + 3] = (byte)a;
            }
        });
        return new PixelImage(r.Width, r.Height, dst);
    }

    /// <summary>指定範囲を乗算済みのまま取り出す。</summary>
    public byte[] CopyPremultiplied(PixelRect region)
    {
        var r = region.Intersect(Rect);
        var dst = new byte[Math.Max(0, r.Width) * Math.Max(0, r.Height) * 4];
        for (int y = 0; y < r.Height; y++)
            Buffer.BlockCopy(Data, ((r.Y + y) * Width + r.X) * 4, dst, y * r.Width * 4, r.Width * 4);
        return dst;
    }
}
