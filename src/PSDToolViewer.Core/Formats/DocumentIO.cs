using PSDToolViewer.Core.Formats.Psd;
using PSDToolViewer.Core.Formats.Ptv;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;
using PSDToolViewer.Core.Rendering;

namespace PSDToolViewer.Core.Formats;

public enum DocumentKind
{
    Unknown,
    Psd,
    Ptv,
    Png,
    Folder,
}

/// <summary>読み込み・保存の窓口。</summary>
public static class DocumentIO
{
    public static DocumentKind DetectKind(string path)
    {
        if (Directory.Exists(path)) return DocumentKind.Folder;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".psd" => DocumentKind.Psd,
            PtvFormat.Extension => DocumentKind.Ptv,
            ".png" => DocumentKind.Png,
            _ => DocumentKind.Unknown,
        };
    }

    public static LayerDocument Load(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return DetectKind(path) switch
            {
                DocumentKind.Psd => PsdReader.Read(path, progress, cancellationToken),
                DocumentKind.Ptv => PtvFormat.Read(path, progress, cancellationToken),
                DocumentKind.Png => ImageImport.LoadPngAsDocument(path),
                DocumentKind.Folder => ImageImport.LoadFolder(path, progress, cancellationToken),
                _ => throw new DocumentFormatException("対応していないファイル形式です。"),
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new DocumentFormatException("ファイルが見つかりません。", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DocumentFormatException("ファイルにアクセスできません。", ex);
        }
    }

    /// <summary>拡張子に応じて PSD または .ptv で保存する。</summary>
    public static void Save(LayerDocument document, string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        switch (DetectKind(path))
        {
            case DocumentKind.Ptv:
                WriteAtomically(path, stream => PtvFormat.Write(document, stream, progress, cancellationToken));
                break;
            case DocumentKind.Psd:
                WritePsd(document, path, null, progress, cancellationToken);
                break;
            default:
                throw new DocumentFormatException("PSD または .ptv の拡張子で保存してください。");
        }
    }

    /// <summary>PSD として書き出す。<paramref name="trimTransparent"/> のとき全レイヤーを重ねた範囲に切り詰める。</summary>
    public static void ExportPsd(LayerDocument document, string path, bool trimTransparent, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        PixelRect? crop = null;
        if (trimTransparent)
        {
            var bounds = document.ComputeAllLayersOpaqueBounds().Intersect(new PixelRect(0, 0, document.Width, document.Height));
            if (!bounds.IsEmpty) crop = bounds;
        }
        WritePsd(document, path, crop, progress, cancellationToken);
    }

    private static void WritePsd(LayerDocument document, string path, PixelRect? crop, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var canvasRect = crop ?? new PixelRect(0, 0, document.Width, document.Height);
        var compositor = new Compositor();
        var full = compositor.Render(RenderTree.Build(document), document.Width, document.Height, cancellationToken);
        var composite = ExtractRegion(full, canvasRect);
        compositor.Release(full);
        WriteAtomically(path, stream => PsdWriter.Write(document, stream, composite, canvasRect, progress, cancellationToken));
    }

    /// <summary>キャンバス外の範囲（トリミングで外側にはみ出す場合）を含めて切り出す。</summary>
    private static PixelImage ExtractRegion(Canvas canvas, PixelRect region)
    {
        var inside = region.Intersect(canvas.Rect);
        var image = canvas.ToStraightImage(inside);
        if (inside == region) return image;
        var data = new byte[region.Width * region.Height * 4];
        for (int y = 0; y < inside.Height; y++)
            Buffer.BlockCopy(image.Data, y * inside.Width * 4, data, ((inside.Y - region.Y + y) * region.Width + (inside.X - region.X)) * 4, inside.Width * 4);
        return new PixelImage(region.Width, region.Height, data);
    }

    /// <summary>一時ファイルに書いてから置き換える（失敗しても元のファイルを壊さない）。</summary>
    private static void WriteAtomically(string path, Action<Stream> write)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
                write(stream);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }
}
