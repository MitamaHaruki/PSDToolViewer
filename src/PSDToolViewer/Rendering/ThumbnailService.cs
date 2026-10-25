using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;
using PSDToolViewer.ViewModels;

namespace PSDToolViewer.Rendering;

/// <summary>レイヤーのサムネイルを裏で作る。</summary>
public sealed class ThumbnailService(Dispatcher dispatcher)
{
    /// <summary>サムネイルの最大サイズ（ピクセル）。高 DPI でもぼやけないよう表示サイズの 2 倍で作る。</summary>
    public const int PixelSize = 56;

    private readonly SemaphoreSlim _gate = new(Math.Max(1, Environment.ProcessorCount / 2));

    /// <summary>表示用の色変換（ドキュメントの色プロファイル → sRGB）。不要なら null。</summary>
    public ColorTransform? Transform { get; set; }

    public void Request(LayerRow row)
    {
        if (row.Node is not LayerItem { Image: var image }) return;
        var transform = Transform;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bitmap = Create(image, transform);
                await dispatcher.InvokeAsync(() => row.Thumbnail = bitmap, DispatcherPriority.Background);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>不透明部分を枠いっぱいに縮小する（面積平均）。</summary>
    public static BitmapSource? Create(PixelImage image, ColorTransform? transform = null)
    {
        var bounds = image.OpaqueBounds;
        if (bounds.IsEmpty) return null;
        double scale = Math.Min(1.0, Math.Min((double)PixelSize / bounds.Width, (double)PixelSize / bounds.Height));
        int tw = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        int th = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        var src = image.Data;
        int stride = image.Width * 4;
        var dst = new byte[tw * th * 4];

        for (int ty = 0; ty < th; ty++)
        {
            int y0 = bounds.Y + ty * bounds.Height / th;
            int y1 = Math.Max(y0 + 1, bounds.Y + (ty + 1) * bounds.Height / th);
            for (int tx = 0; tx < tw; tx++)
            {
                int x0 = bounds.X + tx * bounds.Width / tw;
                int x1 = Math.Max(x0 + 1, bounds.X + (tx + 1) * bounds.Width / tw);
                long b = 0, g = 0, r = 0, a = 0;
                int count = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * stride;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x * 4;
                        int alpha = src[i + 3];
                        b += src[i] * alpha;
                        g += src[i + 1] * alpha;
                        r += src[i + 2] * alpha;
                        a += alpha;
                        count++;
                    }
                }
                int o = (ty * tw + tx) * 4;
                // 乗算済み (Pbgra32) で格納する
                dst[o] = (byte)(b / (255L * count));
                dst[o + 1] = (byte)(g / (255L * count));
                dst[o + 2] = (byte)(r / (255L * count));
                dst[o + 3] = (byte)(a / count);
            }
        }

        transform?.ApplyPremultiplied(dst, dst, tw, th);
        var bitmap = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Pbgra32, null, dst, tw * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
