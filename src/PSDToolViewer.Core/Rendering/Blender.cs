using System.Runtime.CompilerServices;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Rendering;

/// <summary>画素単位の合成処理。描画先は乗算済み BGRA。</summary>
internal static unsafe class Blender
{
    private const int ParallelThreshold = 1 << 15;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Div255(int x) => (x + 128 + ((x + 128) >> 8)) >> 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Unpremultiply(int c, int a) => a == 255 ? c : Math.Min(255, (c * 255 + (a >> 1)) / a);

    /// <summary>非乗算済みの画像を描画する。</summary>
    /// <param name="atop">true のとき描画先の不透明度を保ったまま描く（クリッピング）。</param>
    public static void BlendImage(Canvas dst, PixelImage src, int left, int top, BlendMode mode, int opacity, LayerMask? mask, bool atop)
    {
        if (src.IsEmpty || opacity <= 0) return;
        var rect = new PixelRect(left, top, src.Width, src.Height).Intersect(dst.Rect);
        if (atop) rect = rect.Intersect(dst.Bounds);
        rect = LimitByMask(rect, mask);
        if (rect.IsEmpty) return;
        Run(dst, new Source(src.Data, src.Width, left, top, Premultiplied: false), rect, mode, opacity, mask, atop);
        if (!atop) dst.Bounds = dst.Bounds.Union(rect);
    }

    /// <summary>乗算済みの作業領域を描画する。</summary>
    public static void BlendCanvas(Canvas dst, Canvas src, BlendMode mode, int opacity, LayerMask? mask, bool atop)
    {
        if (opacity <= 0) return;
        var rect = src.Bounds.Intersect(dst.Rect);
        if (atop) rect = rect.Intersect(dst.Bounds);
        rect = LimitByMask(rect, mask);
        if (rect.IsEmpty) return;
        Run(dst, new Source(src.Data, src.Width, 0, 0, Premultiplied: true), rect, mode, opacity, mask, atop);
        if (!atop) dst.Bounds = dst.Bounds.Union(rect);
    }

    /// <summary>
    /// <paramref name="dst"/> を <paramref name="modified"/> に向けて不透明度とマスクの割合で近づける（通過モードのフォルダ用）。
    /// </summary>
    public static void Lerp(Canvas dst, Canvas modified, int opacity, LayerMask? mask)
    {
        var rect = modified.Bounds.Union(dst.Bounds).Intersect(dst.Rect);
        if (rect.IsEmpty || opacity <= 0) return;
        int width = dst.Width;
        ForRows(rect, y =>
        {
            fixed (byte* d = dst.Data, m = modified.Data)
            {
                for (int x = rect.X; x < rect.Right; x++)
                {
                    int k = opacity;
                    if (mask is not null) k = Div255(k * mask.Sample(x, y));
                    if (k == 0) continue;
                    int i = (y * width + x) * 4;
                    for (int c = 0; c < 4; c++)
                        d[i + c] = (byte)(d[i + c] + Div255((m[i + c] - d[i + c]) * k + 255 * 255) - 255);
                }
            }
        });
        dst.Bounds = dst.Bounds.Union(modified.Bounds);
    }

    /// <summary>マスクを掛けて不透明度を下げる。</summary>
    public static void ApplyMask(Canvas canvas, LayerMask mask)
    {
        var rect = canvas.Bounds;
        if (rect.IsEmpty) return;
        int width = canvas.Width;
        ForRows(rect, y =>
        {
            fixed (byte* d = canvas.Data)
            {
                for (int x = rect.X; x < rect.Right; x++)
                {
                    int m = mask.Sample(x, y);
                    if (m == 255) continue;
                    int i = (y * width + x) * 4;
                    d[i] = (byte)Div255(d[i] * m);
                    d[i + 1] = (byte)Div255(d[i + 1] * m);
                    d[i + 2] = (byte)Div255(d[i + 2] * m);
                    d[i + 3] = (byte)Div255(d[i + 3] * m);
                }
            }
        });
    }

    private static PixelRect LimitByMask(PixelRect rect, LayerMask? mask)
        => mask is { DefaultColor: 0 } ? rect.Intersect(mask.Bounds) : rect;

    private static void ForRows(PixelRect rect, Action<int> row)
    {
        if ((long)rect.Width * rect.Height >= ParallelThreshold)
            Parallel.For(rect.Y, rect.Bottom, row);
        else
            for (int y = rect.Y; y < rect.Bottom; y++) row(y);
    }

    private readonly record struct Source(byte[] Data, int Width, int Left, int Top, bool Premultiplied);

    private static void Run(Canvas dst, Source src, PixelRect rect, BlendMode mode, int opacity, LayerMask? mask, bool atop)
    {
        bool normal = BlendFunctions.IsNormal(mode);
        bool nonSeparable = BlendFunctions.IsNonSeparable(mode);
        byte[]? table = normal || nonSeparable ? null : BlendFunctions.GetTable(mode);
        int dstWidth = dst.Width;
        opacity = Math.Clamp(opacity, 0, 255);

        ForRows(rect, y =>
        {
            fixed (byte* d = dst.Data, s = src.Data, lut = table)
            {
                int si = ((y - src.Top) * src.Width + (rect.X - src.Left)) * 4;
                int di = (y * dstWidth + rect.X) * 4;
                for (int x = rect.X; x < rect.Right; x++, si += 4, di += 4)
                {
                    int a = s[si + 3];
                    if (a == 0) continue;

                    int sb = s[si], sg = s[si + 1], sr = s[si + 2];
                    if (src.Premultiplied && a != 255)
                    {
                        sb = Unpremultiply(sb, a);
                        sg = Unpremultiply(sg, a);
                        sr = Unpremultiply(sr, a);
                    }

                    int alpha = opacity == 255 ? a : Div255(a * opacity);
                    if (mask is not null) alpha = Div255(alpha * mask.Sample(x, y));
                    if (alpha == 0) continue;

                    int da = d[di + 3];
                    if (atop && da == 0) continue;

                    if (!normal && da > 0)
                    {
                        int bb = Unpremultiply(d[di], da);
                        int bg = Unpremultiply(d[di + 1], da);
                        int br = Unpremultiply(d[di + 2], da);
                        int rb, rg, rr;
                        if (lut is not null)
                        {
                            rb = lut[(bb << 8) | sb];
                            rg = lut[(bg << 8) | sg];
                            rr = lut[(br << 8) | sr];
                        }
                        else
                        {
                            BlendFunctions.NonSeparable(mode, bb, bg, br, sb, sg, sr, out rb, out rg, out rr);
                        }
                        // 背景が半透明の場合は、元の色と描画モードの結果を背景の不透明度で混ぜる
                        if (da != 255)
                        {
                            int ida = 255 - da;
                            rb = Div255(ida * sb + da * rb);
                            rg = Div255(ida * sg + da * rg);
                            rr = Div255(ida * sr + da * rr);
                        }
                        sb = rb; sg = rg; sr = rr;
                    }

                    int inv = 255 - alpha;
                    if (!atop)
                    {
                        if (alpha == 255)
                        {
                            d[di] = (byte)sb;
                            d[di + 1] = (byte)sg;
                            d[di + 2] = (byte)sr;
                            d[di + 3] = 255;
                        }
                        else
                        {
                            d[di] = (byte)Div255(sb * alpha + d[di] * inv);
                            d[di + 1] = (byte)Div255(sg * alpha + d[di + 1] * inv);
                            d[di + 2] = (byte)Div255(sr * alpha + d[di + 2] * inv);
                            d[di + 3] = (byte)(alpha + Div255(da * inv));
                        }
                    }
                    else
                    {
                        int k = Div255(alpha * da);
                        d[di] = (byte)Div255(sb * k + d[di] * inv);
                        d[di + 1] = (byte)Div255(sg * k + d[di + 1] * inv);
                        d[di + 2] = (byte)Div255(sr * k + d[di + 2] * inv);
                    }
                }
            }
        });
    }
}
