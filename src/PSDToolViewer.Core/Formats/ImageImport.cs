using System.Runtime.InteropServices;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Formats;

/// <summary>PNG を 1 枚のレイヤーとして読み込んだ結果。</summary>
/// <param name="CanvasWidth">元の PNG の幅（キャンバスの拡張に使う）。</param>
public sealed record ImportedLayer(LayerItem Layer, int CanvasWidth, int CanvasHeight);

/// <summary>PNG ファイル・フォルダの読み込み。</summary>
public static partial class ImageImport
{
    /// <summary>
    /// PNG を読み込み、透明な余白を除いたレイヤーにする（位置は左上基準のまま）。
    /// 埋め込みの色プロファイルは sRGB に変換し、<paramref name="toDocument"/> があればさらにドキュメントの色空間に合わせる。
    /// </summary>
    public static ImportedLayer LoadPng(string path, ColorTransform? toDocument = null)
    {
        var image = PngCodec.Decode(path, convertToSrgb: true);
        if (toDocument is not null) image = toDocument.Apply(image);
        return new ImportedLayer(CreateLayer(Path.GetFileNameWithoutExtension(path), image), image.Width, image.Height);
    }

    private static LayerItem CreateLayer(string name, PixelImage image)
    {
        var bounds = image.OpaqueBounds;
        var trimmed = bounds.IsEmpty ? PixelImage.Empty : image.Crop(bounds);
        return new LayerItem(name, trimmed, bounds.IsEmpty ? 0 : bounds.X, bounds.IsEmpty ? 0 : bounds.Y) { IsVisible = true };
    }

    public static LayerDocument LoadPngAsDocument(string path)
    {
        var imported = LoadPng(path);
        var document = new LayerDocument(imported.CanvasWidth, imported.CanvasHeight);
        document.Root.Add(imported.Layer);
        return document;
    }

    /// <summary>
    /// フォルダを読み込む。サブフォルダはフォルダ、PNG はレイヤーになる。名前の自然順で、先頭が一番上（最前面）。
    /// </summary>
    public static LayerDocument LoadFolder(string folder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = new Entry(string.Empty, folder, isGroup: true);
        Scan(root, cancellationToken);

        var files = new List<Entry>();
        CollectFiles(root, files);
        if (files.Count == 0)
            throw new DocumentFormatException("フォルダ内に PNG ファイルが見つかりませんでした。");

        int done = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancellationToken }, entry =>
        {
            try
            {
                entry.Image = PngCodec.Decode(entry.Path, convertToSrgb: true);
            }
            catch (DocumentFormatException ex)
            {
                throw new DocumentFormatException($"「{entry.Path}」を読み込めませんでした。{ex.Message}", ex);
            }
            int n = Interlocked.Increment(ref done);
            progress?.Report(0.95 * n / files.Count);
        });

        int width = files.Max(f => f.Image!.Width);
        int height = files.Max(f => f.Image!.Height);
        var document = new LayerDocument(width, height);
        foreach (var child in root.Children)
            document.Root.Add(Build(child));
        document.NormalizeRadios();
        progress?.Report(1);
        return document;
    }

    private static LayerNode Build(Entry entry)
    {
        if (!entry.IsGroup) return CreateLayer(entry.Name, entry.Image!);
        var group = new LayerGroup(entry.Name) { IsVisible = true, IsExpanded = true };
        foreach (var child in entry.Children)
            group.Add(Build(child));
        return group;
    }

    private static void Scan(Entry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dir = new DirectoryInfo(entry.Path);
        var items = new List<Entry>();
        foreach (var info in dir.EnumerateFileSystemInfos())
        {
            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            if (info is DirectoryInfo sub)
            {
                var child = new Entry(sub.Name, sub.FullName, isGroup: true);
                Scan(child, cancellationToken);
                items.Add(child);
            }
            else if (string.Equals(info.Extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new Entry(Path.GetFileNameWithoutExtension(info.Name), info.FullName, isGroup: false));
            }
        }
        items.Sort((a, b) => CompareNatural(a.SortKey, b.SortKey));
        entry.Children.AddRange(items);
    }

    private static void CollectFiles(Entry entry, List<Entry> files)
    {
        foreach (var child in entry.Children)
        {
            if (child.IsGroup) CollectFiles(child, files);
            else files.Add(child);
        }
    }

    /// <summary>エクスプローラーと同じ並び順（数字を数値として比較）。</summary>
    public static int CompareNatural(string a, string b) => StrCmpLogicalW(a, b);

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string x, string y);

    private sealed class Entry(string name, string path, bool isGroup)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public bool IsGroup { get; } = isGroup;
        public string SortKey => System.IO.Path.GetFileName(Path);
        public List<Entry> Children { get; } = [];
        public PixelImage? Image { get; set; }
    }
}
