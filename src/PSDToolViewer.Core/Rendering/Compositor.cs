using System.Collections.Concurrent;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Rendering;

/// <summary>レイヤーを合成して 1 枚の画像にする。作業領域を使い回すため、インスタンスを保持して使う。</summary>
public sealed class Compositor
{
    private readonly ConcurrentBag<Canvas> _pool = [];

    /// <summary>
    /// 合成する。戻り値は使い終わったら <see cref="Release"/> で返却する（返却しなくても問題はない）。
    /// </summary>
    public Canvas Render(RenderGroup root, int width, int height, CancellationToken cancellationToken = default)
    {
        var target = Rent(width, height);
        try
        {
            RenderChildren(root.Children, target, cancellationToken);
            return target;
        }
        catch
        {
            Release(target);
            throw;
        }
    }

    public static Canvas RenderOnce(LayerDocument document)
        => new Compositor().Render(RenderTree.Build(document), document.Width, document.Height);

    public void Release(Canvas canvas)
    {
        canvas.Clear();
        if (_pool.Count < 8) _pool.Add(canvas);
    }

    private Canvas Rent(int width, int height)
    {
        while (_pool.TryTake(out var canvas))
        {
            if (canvas.Width == width && canvas.Height == height) return canvas;
        }
        return new Canvas(width, height);
    }

    private void RenderChildren(RenderNode[] children, Canvas target, CancellationToken ct)
    {
        int i = 0;
        while (i < children.Length)
        {
            ct.ThrowIfCancellationRequested();
            var baseNode = children[i];
            int j = i + 1;
            while (j < children.Length && children[j].Clipping) j++;

            if (baseNode.Visible)
            {
                bool hasClipped = false;
                for (int k = i + 1; k < j; k++)
                    if (children[k].Visible) { hasClipped = true; break; }

                if (hasClipped)
                    RenderClippingGroup(baseNode, children.AsSpan(i + 1, j - i - 1).ToArray(), target, ct);
                else
                    RenderNode(baseNode, target, ct);
            }
            i = j;
        }
    }

    private void RenderNode(RenderNode node, Canvas target, CancellationToken ct)
    {
        switch (node)
        {
            case RenderLayer layer:
                Blender.BlendImage(target, layer.Image, layer.Left, layer.Top, layer.Mode, layer.Opacity, layer.Mask, atop: false);
                break;

            case RenderGroup group when group.Mode == BlendMode.PassThrough:
                if (group.Opacity >= 255 && group.Mask is null)
                {
                    RenderChildren(group.Children, target, ct);
                }
                else
                {
                    // 下の画像に直接重ねた結果へ、不透明度の割合で近づける
                    var copy = Rent(target.Width, target.Height);
                    try
                    {
                        copy.CopyFrom(target);
                        RenderChildren(group.Children, copy, ct);
                        Blender.Lerp(target, copy, group.Opacity, group.Mask);
                    }
                    finally
                    {
                        Release(copy);
                    }
                }
                break;

            case RenderGroup group:
            {
                var buffer = Rent(target.Width, target.Height);
                try
                {
                    RenderChildren(group.Children, buffer, ct);
                    Blender.BlendCanvas(target, buffer, group.Mode, group.Opacity, group.Mask, atop: false);
                }
                finally
                {
                    Release(buffer);
                }
                break;
            }
        }
    }

    /// <summary>
    /// クリッピング：土台を単独で描き、その不透明部分にだけクリッピングされた項目を重ねてから、
    /// 土台の描画モードと不透明度で下に合成する。
    /// </summary>
    private void RenderClippingGroup(RenderNode baseNode, RenderNode[] clipped, Canvas target, CancellationToken ct)
    {
        var buffer = Rent(target.Width, target.Height);
        try
        {
            switch (baseNode)
            {
                case RenderLayer layer:
                    Blender.BlendImage(buffer, layer.Image, layer.Left, layer.Top, BlendMode.Normal, 255, layer.Mask, atop: false);
                    break;
                case RenderGroup group:
                    RenderChildren(group.Children, buffer, ct);
                    if (group.Mask is not null) Blender.ApplyMask(buffer, group.Mask);
                    break;
            }

            foreach (var node in clipped)
            {
                if (!node.Visible) continue;
                var mode = node.Mode == BlendMode.PassThrough ? BlendMode.Normal : node.Mode;
                switch (node)
                {
                    case RenderLayer layer:
                        Blender.BlendImage(buffer, layer.Image, layer.Left, layer.Top, mode, layer.Opacity, layer.Mask, atop: true);
                        break;
                    case RenderGroup group:
                    {
                        var inner = Rent(target.Width, target.Height);
                        try
                        {
                            RenderChildren(group.Children, inner, ct);
                            Blender.BlendCanvas(buffer, inner, mode, group.Opacity, group.Mask, atop: true);
                        }
                        finally
                        {
                            Release(inner);
                        }
                        break;
                    }
                }
            }

            var baseMode = baseNode.Mode == BlendMode.PassThrough ? BlendMode.Normal : baseNode.Mode;
            Blender.BlendCanvas(target, buffer, baseMode, baseNode.Opacity, null, atop: false);
        }
        finally
        {
            Release(buffer);
        }
    }
}
