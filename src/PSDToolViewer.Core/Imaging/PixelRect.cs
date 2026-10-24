namespace PSDToolViewer.Core.Imaging;

/// <summary>整数座標の矩形。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static readonly PixelRect Empty = new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromEdges(int left, int top, int right, int bottom)
        => right <= left || bottom <= top ? Empty : new PixelRect(left, top, right - left, bottom - top);

    public PixelRect Intersect(PixelRect other)
    {
        if (IsEmpty || other.IsEmpty) return Empty;
        return FromEdges(Math.Max(X, other.X), Math.Max(Y, other.Y), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
    }

    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        return FromEdges(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
    }

    public PixelRect Offset(int dx, int dy) => IsEmpty ? Empty : new PixelRect(X + dx, Y + dy, Width, Height);
}
