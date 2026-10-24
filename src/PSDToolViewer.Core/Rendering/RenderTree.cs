using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Rendering;

/// <summary>
/// 合成用に固定したツリー。UI スレッドで作成し、別スレッドで合成しても編集の影響を受けない。
/// </summary>
public abstract record RenderNode(bool Visible, BlendMode Mode, int Opacity, LayerMask? Mask, bool Clipping);

public sealed record RenderLayer(bool Visible, BlendMode Mode, int Opacity, LayerMask? Mask, bool Clipping, PixelImage Image, int Left, int Top)
    : RenderNode(Visible, Mode, Opacity, Mask, Clipping);

/// <summary>フォルダ。<see cref="Children"/> は下（背面）から上（前面）の順。</summary>
public sealed record RenderGroup(bool Visible, BlendMode Mode, int Opacity, LayerMask? Mask, bool Clipping, RenderNode[] Children)
    : RenderNode(Visible, Mode, Opacity, Mask, Clipping);

public static class RenderTree
{
    public static RenderGroup Build(LayerDocument document)
        => new(true, BlendMode.PassThrough, 255, null, false, BuildChildren(document.Root));

    private static RenderNode[] BuildChildren(LayerGroup group)
    {
        var result = new RenderNode[group.Children.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = Build(group.Children[group.Children.Count - 1 - i]);
        return result;
    }

    private static RenderNode Build(LayerNode node)
    {
        int opacity = node.Opacity * node.FillOpacity / 255;
        var mask = node.Mask is { Disabled: false } m ? m : null;
        return node switch
        {
            LayerGroup g => new RenderGroup(g.IsShown, g.BlendMode, opacity, mask, g.Clipping, BuildChildren(g)),
            LayerItem l => new RenderLayer(l.IsShown, l.BlendMode, opacity, mask, l.Clipping, l.Image, l.Left, l.Top),
            _ => throw new NotSupportedException(),
        };
    }
}
