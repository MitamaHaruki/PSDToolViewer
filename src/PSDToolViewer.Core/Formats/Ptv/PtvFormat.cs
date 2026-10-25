using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;
using PSDToolViewer.Core.Rendering;

namespace PSDToolViewer.Core.Formats.Ptv;

/// <summary>
/// 専用形式（.ptv）。ZIP の中に document.json と、レイヤー・マスクの PNG を格納する。
/// 先頭には保存時の見た目の縮小画像（thumbnail.png、sRGB、無圧縮で格納）を置き、エクスプローラーのサムネイルに使う。
/// </summary>
public static class PtvFormat
{
    public const string Extension = ".ptv";
    public const string ThumbnailEntry = "thumbnail.png";
    public const int ThumbnailSize = 512;
    private const string DocumentEntry = "document.json";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void Write(LayerDocument document, Stream output, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        // 画像の PNG 化は時間がかかるため先に並列で行う
        var layers = document.EnumerateNodes().ToList();
        var images = new byte[layers.Count][];
        var masks = new byte[layers.Count][];
        int done = 0;
        Parallel.For(0, layers.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            if (layers[i] is LayerItem { Image.IsEmpty: false } item) images[i] = PngCodec.Encode(item.Image);
            if (layers[i].Mask is { Bounds.IsEmpty: false } mask) masks[i] = PngCodec.EncodeGray(mask.Bounds.Width, mask.Bounds.Height, mask.Data);
            int n = Interlocked.Increment(ref done);
            progress?.Report(0.9 * n / Math.Max(1, layers.Count));
        });

        var index = layers.Select((node, i) => (node, i)).ToDictionary(p => p.node, p => p.i);
        var model = new DocumentModel
        {
            Version = CurrentVersion,
            Width = document.Width,
            Height = document.Height,
            Children = document.Root.Children.Select(n => ToModel(n, index, images, masks)).ToList(),
            Resources = document.ImageResources.Count == 0
                ? null
                : document.ImageResources.Select(r => new ResourceModel { Id = r.Id, Name = r.Name, Data = Convert.ToBase64String(r.Data) }).ToList(),
        };

        var thumbnail = CreateThumbnail(document, cancellationToken);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        if (thumbnail is not null) WriteEntry(zip, ThumbnailEntry, thumbnail);
        var entry = zip.CreateEntry(DocumentEntry, CompressionLevel.Optimal);
        using (var stream = entry.Open())
            JsonSerializer.Serialize(stream, model, JsonOptions);

        for (int i = 0; i < layers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // PNG は圧縮済みのため格納のみ
            if (images[i] is { } png) WriteEntry(zip, ImagePath(i), png);
            if (masks[i] is { } maskPng) WriteEntry(zip, MaskPath(i), maskPng);
        }
        progress?.Report(1);
    }

    /// <summary>保存時の見た目（表示状態どおりの合成結果）を縮小した PNG。色プロファイルがあれば sRGB に変換する。</summary>
    private static byte[]? CreateThumbnail(LayerDocument document, CancellationToken cancellationToken)
    {
        if (document.Width <= 0 || document.Height <= 0) return null;
        var compositor = new Compositor();
        var canvas = compositor.Render(RenderTree.Build(document), document.Width, document.Height, cancellationToken);
        var image = ImageScaler.Fit(canvas.ToStraightImage(), ThumbnailSize);
        compositor.Release(canvas);
        if (document.ColorProfile is { } profile && ColorTransform.Create(profile, null) is { } transform)
            image = transform.Apply(image);
        return PngCodec.Encode(image);
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] data)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static string ImagePath(int i) => $"layers/{i:D5}.png";
    private static string MaskPath(int i) => $"masks/{i:D5}.png";

    private static NodeModel ToModel(LayerNode node, Dictionary<LayerNode, int> index, byte[][] images, byte[][] masks)
    {
        int i = index[node];
        var model = new NodeModel
        {
            Name = node.InternalName,
            Visible = node.IsVisible,
            Opacity = node.Opacity == 255 ? null : node.Opacity,
            FillOpacity = node.FillOpacity == 255 ? null : node.FillOpacity,
            BlendMode = node.BlendMode,
            Clipping = node.Clipping ? true : null,
        };
        if (node.Mask is { } mask)
        {
            model.Mask = new MaskModel
            {
                X = mask.Bounds.X,
                Y = mask.Bounds.Y,
                Width = mask.Bounds.Width,
                Height = mask.Bounds.Height,
                DefaultColor = mask.DefaultColor,
                Disabled = mask.Disabled ? true : null,
                Image = masks[i] is null ? null : MaskPath(i),
            };
        }
        switch (node)
        {
            case LayerGroup group:
                model.Type = "group";
                model.Expanded = group.IsExpanded;
                model.Children = group.Children.Select(c => ToModel(c, index, images, masks)).ToList();
                break;
            case LayerItem item:
                model.Type = "layer";
                model.Left = item.Left;
                model.Top = item.Top;
                model.Image = images[i] is null ? null : ImagePath(i);
                break;
        }
        return model;
    }

    public static LayerDocument Read(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, progress, cancellationToken);
    }

    public static LayerDocument Read(Stream input, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
            var docEntry = zip.GetEntry(DocumentEntry) ?? throw new DocumentFormatException("PSDTool Viewer のファイルではありません。");
            DocumentModel model;
            using (var stream = docEntry.Open())
                model = JsonSerializer.Deserialize<DocumentModel>(stream, JsonOptions) ?? throw new DocumentFormatException("ファイルの内容が不正です。");
            if (model.Version > CurrentVersion)
                throw new DocumentFormatException("新しいバージョンの PSDTool Viewer で保存されたファイルです。アプリを更新してください。");
            if (model.Width <= 0 || model.Height <= 0)
                throw new DocumentFormatException("画像サイズが不正です。");

            // 画像の展開を並列で行うため、先に ZIP から読み出しておく
            var blobs = new Dictionary<string, byte[]>();
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == DocumentEntry) continue;
                using var s = entry.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                blobs[entry.FullName] = ms.ToArray();
            }

            var nodes = new List<NodeModel>();
            Collect(model.Children, nodes);
            var images = new PixelImage?[nodes.Count];
            var masks = new LayerMask?[nodes.Count];
            int done = 0;
            Parallel.For(0, nodes.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
            {
                var node = nodes[i];
                if (node.Image is { } imagePath && blobs.TryGetValue(imagePath, out var png))
                {
                    using var ms = new MemoryStream(png);
                    images[i] = PngCodec.Decode(ms);
                }
                if (node.Mask is { } m)
                {
                    var data = new byte[Math.Max(0, m.Width) * Math.Max(0, m.Height)];
                    if (m.Image is { } maskPath && blobs.TryGetValue(maskPath, out var maskPng))
                    {
                        using var ms = new MemoryStream(maskPng);
                        var decoded = PngCodec.DecodeGray(ms, out int w, out int h);
                        if (w == m.Width && h == m.Height) data = decoded;
                    }
                    masks[i] = new LayerMask(new PixelRect(m.X, m.Y, m.Width, m.Height), data, m.DefaultColor, m.Disabled ?? false);
                }
                int n = Interlocked.Increment(ref done);
                progress?.Report(0.1 + 0.85 * n / Math.Max(1, nodes.Count));
            });

            var document = new LayerDocument(model.Width, model.Height);
            int next = 0;
            foreach (var child in model.Children ?? [])
                document.Root.Add(FromModel(child, images, masks, ref next));

            document.ImageResources = model.Resources?
                .Select(r => new ImageResource(r.Id, r.Name ?? string.Empty, Convert.FromBase64String(r.Data ?? string.Empty)))
                .ToList() ?? [];
            document.NormalizeRadios();
            progress?.Report(1);
            return document;
        }
        catch (InvalidDataException ex)
        {
            throw new DocumentFormatException("ファイルが壊れているか、PSDTool Viewer のファイルではありません。", ex);
        }
        catch (JsonException ex)
        {
            throw new DocumentFormatException("ファイルの内容が不正です。", ex);
        }
    }

    /// <summary>行きがけ順に並べる（<see cref="FromModel"/> と同じ順序）。</summary>
    private static void Collect(List<NodeModel>? children, List<NodeModel> result)
    {
        foreach (var child in children ?? [])
        {
            result.Add(child);
            if (child.Type == "group") Collect(child.Children, result);
        }
    }

    private static LayerNode FromModel(NodeModel model, PixelImage?[] images, LayerMask?[] masks, ref int index)
    {
        int i = index++;
        LayerNode node;
        if (model.Type == "group")
        {
            var group = new LayerGroup(model.Name ?? string.Empty)
            {
                IsExpanded = model.Expanded ?? true,
                BlendMode = model.BlendMode ?? BlendMode.PassThrough,
            };
            foreach (var child in model.Children ?? [])
                group.Add(FromModel(child, images, masks, ref index));
            node = group;
        }
        else
        {
            node = new LayerItem(model.Name ?? string.Empty, images[i] ?? PixelImage.Empty, model.Left ?? 0, model.Top ?? 0)
            {
                BlendMode = model.BlendMode ?? BlendMode.Normal,
            };
        }
        node.IsVisible = model.Visible;
        node.Opacity = model.Opacity ?? 255;
        node.FillOpacity = model.FillOpacity ?? 255;
        node.Clipping = model.Clipping ?? false;
        node.Mask = masks[i];
        return node;
    }

    private sealed class DocumentModel
    {
        public int Version { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<NodeModel>? Children { get; set; }
        public List<ResourceModel>? Resources { get; set; }
    }

    private sealed class NodeModel
    {
        public string Type { get; set; } = "layer";
        public string? Name { get; set; }
        public bool Visible { get; set; } = true;
        public byte? Opacity { get; set; }
        public byte? FillOpacity { get; set; }
        public BlendMode? BlendMode { get; set; }
        public bool? Clipping { get; set; }
        public int? Left { get; set; }
        public int? Top { get; set; }
        public string? Image { get; set; }
        public MaskModel? Mask { get; set; }
        public bool? Expanded { get; set; }
        public List<NodeModel>? Children { get; set; }
    }

    private sealed class MaskModel
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public byte DefaultColor { get; set; }
        public bool? Disabled { get; set; }
        public string? Image { get; set; }
    }

    private sealed class ResourceModel
    {
        public ushort Id { get; set; }
        public string? Name { get; set; }
        public string? Data { get; set; }
    }
}
