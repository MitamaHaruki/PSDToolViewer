namespace PSDToolViewer.Core.Imaging;

/// <summary>
/// 非乗算済み BGRA (8bit) の画像。生成後は変更しない前提で、スレッド間で共有できる。
/// </summary>
public sealed class PixelImage
{
    public static readonly PixelImage Empty = new(0, 0, []);

    private PixelRect? _opaqueBounds;

    public PixelImage(int width, int height, byte[] data)
    {
        if (data.Length < width * height * 4) throw new ArgumentException("画像データのサイズが不足しています。", nameof(data));
        Width = width;
        Height = height;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;

    /// <summary>BGRA の画素データ（非乗算済み）。</summary>
    public byte[] Data { get; }

    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>不透明な画素（α&gt;0）を含む最小矩形。完全に透明なら空矩形。</summary>
    public PixelRect OpaqueBounds => _opaqueBounds ??= ComputeOpaqueBounds();

    private PixelRect ComputeOpaqueBounds()
    {
        int w = Width, h = Height;
        if (w == 0 || h == 0) return PixelRect.Empty;
        var data = Data;
        int top = -1, bottom = -1;
        for (int y = 0; y < h && top < 0; y++)
            if (RowHasAlpha(data, y * w * 4, w)) top = y;
        if (top < 0) return PixelRect.Empty;
        for (int y = h - 1; y >= top; y--)
            if (RowHasAlpha(data, y * w * 4, w)) { bottom = y; break; }

        int left = w, right = -1;
        for (int y = top; y <= bottom; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < left; x++)
                if (data[row + x * 4 + 3] != 0) { left = x; break; }
            for (int x = w - 1; x > right; x--)
                if (data[row + x * 4 + 3] != 0) { right = x; break; }
        }
        return PixelRect.FromEdges(left, top, right + 1, bottom + 1);
    }

    private static bool RowHasAlpha(byte[] data, int offset, int width)
    {
        for (int x = 0; x < width; x++)
            if (data[offset + x * 4 + 3] != 0) return true;
        return false;
    }

    /// <summary>指定範囲を切り出した新しい画像を返す。</summary>
    public PixelImage Crop(PixelRect rect)
    {
        rect = rect.Intersect(new PixelRect(0, 0, Width, Height));
        if (rect.IsEmpty) return Empty;
        if (rect.X == 0 && rect.Y == 0 && rect.Width == Width && rect.Height == Height) return this;
        var dst = new byte[rect.Width * rect.Height * 4];
        for (int y = 0; y < rect.Height; y++)
            Buffer.BlockCopy(Data, ((rect.Y + y) * Width + rect.X) * 4, dst, y * rect.Width * 4, rect.Width * 4);
        return new PixelImage(rect.Width, rect.Height, dst);
    }
}
