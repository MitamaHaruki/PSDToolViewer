namespace PSDToolViewer.Core.Imaging;

/// <summary>
/// レイヤーマスク（グレースケール）。<see cref="Bounds"/> はドキュメント座標、範囲外は <see cref="DefaultColor"/>。
/// </summary>
public sealed class LayerMask
{
    public LayerMask(PixelRect bounds, byte[] data, byte defaultColor, bool disabled = false)
    {
        if (data.Length < Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height))
            throw new ArgumentException("マスクデータのサイズが不足しています。", nameof(data));
        Bounds = bounds;
        Data = data;
        DefaultColor = defaultColor;
        Disabled = disabled;
    }

    public PixelRect Bounds { get; }
    public byte[] Data { get; }
    public byte DefaultColor { get; }

    /// <summary>Photoshop 上で「マスクを使用しない」状態。合成時は無視するが保存時は維持する。</summary>
    public bool Disabled { get; }

    public byte Sample(int x, int y)
    {
        var b = Bounds;
        if (x < b.X || y < b.Y || x >= b.Right || y >= b.Bottom) return DefaultColor;
        return Data[(y - b.Y) * b.Width + (x - b.X)];
    }

    public LayerMask Offset(int dx, int dy) => new(Bounds.Offset(dx, dy), Data, DefaultColor, Disabled);
}
