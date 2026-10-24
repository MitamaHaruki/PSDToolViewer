using PSDToolViewer.Core.Imaging;

namespace PSDToolViewer.Core.Model;

/// <summary>PSD の画像リソース（保存時に引き継ぐもの）。</summary>
public sealed record ImageResource(ushort Id, string Name, byte[] Data);

/// <summary>読み込んだドキュメント全体。</summary>
public sealed class LayerDocument
{
    public LayerDocument(int width, int height)
    {
        Width = width;
        Height = height;
        Root = new LayerGroup(string.Empty, isRoot: true);
    }

    public int Width { get; set; }
    public int Height { get; set; }

    public LayerGroup Root { get; }

    /// <summary>PSD から引き継ぐ画像リソース（解像度・カラープロファイル）。</summary>
    public IReadOnlyList<ImageResource> ImageResources { get; set; } = [];

    /// <summary>再現できない要素（調整レイヤー・レイヤー効果など）を含む PSD を読み込んだか。</summary>
    public bool HasUnsupportedFeatures { get; set; }

    public bool IsEmpty => Root.Children.Count == 0;

    /// <summary>すべてのノードを画面の並び順（行きがけ順）で列挙する。</summary>
    public IEnumerable<LayerNode> EnumerateNodes() => Enumerate(Root);

    private static IEnumerable<LayerNode> Enumerate(LayerGroup group)
    {
        foreach (var child in group.Children)
        {
            yield return child;
            if (child is LayerGroup g)
                foreach (var n in Enumerate(g)) yield return n;
        }
    }

    public IEnumerable<LayerGroup> EnumerateGroups(bool includeRoot = true)
    {
        if (includeRoot) yield return Root;
        foreach (var node in EnumerateNodes())
            if (node is LayerGroup g) yield return g;
    }

    public void NormalizeRadios()
    {
        foreach (var group in EnumerateGroups())
            group.NormalizeRadios();
    }

    /// <summary>キャンバスが指定範囲（左上基準）を含むように広げる。</summary>
    public bool EnsureCanvasContains(PixelRect rect)
    {
        if (rect.IsEmpty) return false;
        int w = Math.Max(Width, rect.Right);
        int h = Math.Max(Height, rect.Bottom);
        if (w == Width && h == Height) return false;
        Width = w;
        Height = h;
        return true;
    }

    /// <summary>全レイヤーの不透明部分を合わせた範囲（表示状態は問わない）。</summary>
    public PixelRect ComputeAllLayersOpaqueBounds()
    {
        var bounds = PixelRect.Empty;
        foreach (var node in EnumerateNodes())
            if (node is LayerItem layer) bounds = bounds.Union(layer.OpaqueBounds);
        return bounds;
    }
}
