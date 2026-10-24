using PSDToolViewer.Core.Imaging;

namespace PSDToolViewer.Core.Model;

/// <summary>画像を持つレイヤー。</summary>
public sealed class LayerItem : LayerNode
{
    public LayerItem(string internalName, PixelImage image, int left, int top)
        : base(internalName)
    {
        Image = image;
        Left = left;
        Top = top;
    }

    public PixelImage Image { get; }

    /// <summary>画像左上のドキュメント座標。</summary>
    public int Left { get; }
    public int Top { get; }

    public PixelRect Bounds => new(Left, Top, Image.Width, Image.Height);

    /// <summary>実際に不透明な画素がある範囲（ドキュメント座標）。</summary>
    public PixelRect OpaqueBounds => Image.OpaqueBounds.Offset(Left, Top);

    public override bool IsGroup => false;
}
