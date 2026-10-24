using System.IO.Compression;
using System.Text;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Formats.Psd;

/// <summary>PSD ファイルの読み込み（8bit RGB のみ）。</summary>
public static class PsdReader
{
    /// <summary>保存時に引き継ぐ画像リソース：解像度、ICC プロファイル。</summary>
    internal static readonly ushort[] PreservedResourceIds = [0x03ED, 0x040F];

    /// <summary>再現しない要素を表す追加情報のキー。</summary>
    private static readonly HashSet<string> UnsupportedKeys =
    [
        "TySh", "tySh", "lfx2", "lrFX", "lfxs", "lmfx", "SoLd", "PlLd", "SoLE", "vmsk", "vsms",
        "SoCo", "GdFl", "PtFl", "brit", "levl", "curv", "expA", "vibA", "hue ", "hue2", "blnc",
        "blwh", "phfl", "mixr", "clrL", "nvrt", "post", "thrs", "grdm", "selc",
    ];

    public static LayerDocument Read(string path, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (OutOfMemoryException ex)
        {
            throw new DocumentFormatException("ファイルが大きすぎるため読み込めません。", ex);
        }
        return Read(data, progress, cancellationToken);
    }

    public static LayerDocument Read(byte[] data, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var r = new BigEndianReader(data);

        // ヘッダー
        if (r.Length < 26 || r.ReadAscii(4) != "8BPS")
            throw new DocumentFormatException("PSD ファイルではありません。");
        ushort version = r.ReadUInt16();
        if (version == 2)
            throw new DocumentFormatException("PSB（大きなドキュメント形式）には対応していません。");
        if (version != 1)
            throw new DocumentFormatException("対応していない PSD のバージョンです。");
        r.Skip(6);
        int channelCount = r.ReadUInt16();
        int height = r.ReadInt32();
        int width = r.ReadInt32();
        int depth = r.ReadUInt16();
        int colorMode = r.ReadUInt16();
        if (depth != 8)
            throw new DocumentFormatException($"{depth}bit の PSD には対応していません。8bit の PSD を使用してください。");
        if (colorMode != 3)
            throw new DocumentFormatException("RGB カラー以外の PSD には対応していません。");
        if (width <= 0 || height <= 0)
            throw new DocumentFormatException("画像サイズが不正です。");

        var document = new LayerDocument(width, height);

        // カラーモードデータ
        r.Skip(r.ReadUInt32());

        // 画像リソース
        uint resourcesLength = r.ReadUInt32();
        long resourcesEnd = r.Position + resourcesLength;
        document.ImageResources = ReadImageResources(r, resourcesEnd);
        r.Seek(resourcesEnd);

        // レイヤーとマスク情報
        uint layerSectionLength = r.ReadUInt32();
        long layerSectionEnd = r.Position + layerSectionLength;
        var records = new List<LayerRecord>();
        bool unsupported = false;
        if (layerSectionLength > 0)
        {
            uint layerInfoLength = r.ReadUInt32();
            long layerInfoEnd = r.Position + layerInfoLength;
            if (layerInfoLength > 0)
                records = ReadLayerInfo(r, ref unsupported);
            r.Seek(Math.Min(layerInfoEnd, layerSectionEnd));

            // グローバルレイヤーマスク
            if (r.Position + 4 <= layerSectionEnd)
            {
                uint globalMaskLength = r.ReadUInt32();
                r.Seek(Math.Min(r.Position + globalMaskLength, layerSectionEnd));
            }

            // 追加情報（レイヤー情報がここに格納されている場合がある）
            while (r.Position + 12 <= layerSectionEnd && records.Count == 0)
            {
                string sig = r.ReadAscii(4);
                if (sig != "8BIM" && sig != "8B64") break;
                string key = r.ReadAscii(4);
                uint length = r.ReadUInt32();
                long blockEnd = r.Position + length;
                if (key == "Layr" && length > 0)
                    records = ReadLayerInfo(r, ref unsupported);
                r.Seek(Math.Min(AlignBlockEnd(r, blockEnd, layerSectionEnd), layerSectionEnd));
            }
        }
        document.HasUnsupportedFeatures = unsupported;

        cancellationToken.ThrowIfCancellationRequested();

        if (records.Count == 0)
        {
            r.Seek(layerSectionEnd);
            var merged = ReadMergedImage(r, width, height, channelCount);
            document.Root.Add(new LayerItem("背景", merged, 0, 0));
            progress?.Report(1);
            return document;
        }

        DecodeChannels(r, records, progress, cancellationToken);
        BuildTree(document, records);
        document.NormalizeRadios();
        progress?.Report(1);
        return document;
    }

    private static List<ImageResource> ReadImageResources(BigEndianReader r, long end)
    {
        var list = new List<ImageResource>();
        while (r.Position + 12 <= end)
        {
            if (r.ReadAscii(4) != "8BIM") break;
            ushort id = r.ReadUInt16();
            string name = ReadPascalString(r, 2);
            uint size = r.ReadUInt32();
            if (r.Position + size > end) break;
            byte[] data = r.ReadBytes((int)size);
            if ((size & 1) != 0) r.Skip(1);
            if (PreservedResourceIds.Contains(id))
                list.Add(new ImageResource(id, name, data));
        }
        return list;
    }

    private static List<LayerRecord> ReadLayerInfo(BigEndianReader r, ref bool unsupported)
    {
        int count = Math.Abs((int)r.ReadInt16());
        var records = new List<LayerRecord>(count);
        for (int i = 0; i < count; i++)
            records.Add(ReadLayerRecord(r, ref unsupported));

        // チャンネルの画像データは後でまとめて展開するため、位置だけ記録する
        foreach (var record in records)
        {
            foreach (var channel in record.Channels)
            {
                channel.DataOffset = r.Position;
                r.Skip(channel.Length);
            }
        }
        return records;
    }

    private static LayerRecord ReadLayerRecord(BigEndianReader r, ref bool unsupported)
    {
        var record = new LayerRecord();
        int top = r.ReadInt32(), left = r.ReadInt32(), bottom = r.ReadInt32(), right = r.ReadInt32();
        record.Bounds = PixelRect.FromEdges(left, top, right, bottom);

        int channelCount = r.ReadUInt16();
        for (int i = 0; i < channelCount; i++)
            record.Channels.Add(new ChannelInfo { Id = r.ReadInt16(), Length = r.ReadUInt32() });

        if (r.ReadAscii(4) != "8BIM")
            throw new DocumentFormatException("レイヤー情報が壊れています。");
        record.BlendKey = r.ReadAscii(4);
        record.Opacity = r.ReadByte();
        record.Clipping = r.ReadByte() != 0;
        record.Flags = r.ReadByte();
        r.Skip(1);

        uint extraLength = r.ReadUInt32();
        long extraEnd = r.Position + extraLength;

        ReadMaskData(r, record);

        uint blendingRangesLength = r.ReadUInt32();
        r.Skip(blendingRangesLength);

        record.LegacyName = ReadPascalString(r, 4);

        while (r.Position + 12 <= extraEnd)
        {
            string sig = r.ReadAscii(4);
            if (sig != "8BIM" && sig != "8B64") break;
            string key = r.ReadAscii(4);
            uint length = r.ReadUInt32();
            long blockStart = r.Position;
            long blockEnd = blockStart + length;
            if (blockEnd > extraEnd) break;

            switch (key)
            {
                case "luni":
                    if (length >= 4)
                    {
                        int chars = (int)r.ReadUInt32();
                        chars = (int)Math.Min(chars, (length - 4) / 2);
                        record.UnicodeName = Encoding.BigEndianUnicode.GetString(r.ReadSpan(chars * 2)).TrimEnd('\0');
                    }
                    break;
                case "lsct":
                case "lsdk":
                    if (length >= 4)
                    {
                        record.SectionType = (int)r.ReadUInt32();
                        if (length >= 12 && r.ReadAscii(4) == "8BIM")
                            record.SectionBlendKey = r.ReadAscii(4);
                    }
                    break;
                case "iOpa":
                    if (length >= 1) record.FillOpacity = r.ReadByte();
                    break;
                default:
                    if (UnsupportedKeys.Contains(key)) unsupported = true;
                    break;
            }
            r.Seek(AlignBlockEnd(r, blockEnd, extraEnd));
        }

        r.Seek(extraEnd);
        return record;
    }

    /// <summary>追加情報ブロックの終端。奇数長で詰め物がある場合に次の署名の位置へ合わせる。</summary>
    private static long AlignBlockEnd(BigEndianReader r, long blockEnd, long limit)
    {
        for (long p = blockEnd; p <= blockEnd + 3 && p + 4 <= limit; p++)
            if (r.PeekAscii(p, "8BIM") || r.PeekAscii(p, "8B64")) return p;
        return blockEnd;
    }

    private static void ReadMaskData(BigEndianReader r, LayerRecord record)
    {
        uint length = r.ReadUInt32();
        long end = r.Position + length;
        if (length >= 18)
        {
            int top = r.ReadInt32(), left = r.ReadInt32(), bottom = r.ReadInt32(), right = r.ReadInt32();
            record.MaskBounds = PixelRect.FromEdges(left, top, right, bottom);
            record.MaskDefaultColor = r.ReadByte();
            record.MaskFlags = r.ReadByte();
            record.HasMaskData = true;

            if (length > 20)
            {
                if ((record.MaskFlags & 0x10) != 0 && r.Position < end)
                {
                    byte parameters = r.ReadByte();
                    if ((parameters & 0x01) != 0) r.Skip(1);
                    if ((parameters & 0x02) != 0) r.Skip(8);
                    if ((parameters & 0x04) != 0) r.Skip(1);
                    if ((parameters & 0x08) != 0) r.Skip(8);
                }
                if (r.Position + 18 <= end)
                {
                    r.Skip(1); // real flags
                    record.RealMaskDefaultColor = r.ReadByte();
                    int rt = r.ReadInt32(), rl = r.ReadInt32(), rb = r.ReadInt32(), rr = r.ReadInt32();
                    record.RealMaskBounds = PixelRect.FromEdges(rl, rt, rr, rb);
                }
            }
        }
        r.Seek(end);
    }

    private static string ReadPascalString(BigEndianReader r, int padding)
    {
        int length = r.ReadByte();
        var bytes = r.ReadSpan(length);
        int total = length + 1;
        while (total % padding != 0) { r.Skip(1); total++; }
        return LegacyEncoding.Decode(bytes);
    }

    private static void DecodeChannels(BigEndianReader r, List<LayerRecord> records, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        int done = 0;
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.ForEach(records, options, record =>
        {
            DecodeLayer(r.Data, record);
            int n = Interlocked.Increment(ref done);
            progress?.Report(0.05 + 0.9 * n / records.Count);
        });
    }

    private static void DecodeLayer(byte[] data, LayerRecord record)
    {
        if (record.SectionType is 1 or 2 or 3)
        {
            // フォルダの区切り。マスクだけ取り出す
            record.Mask = DecodeMask(data, record);
            return;
        }

        var bounds = record.Bounds;
        int w = Math.Max(0, bounds.Width), h = Math.Max(0, bounds.Height);
        byte[]? red = null, green = null, blue = null, alpha = null;
        foreach (var channel in record.Channels)
        {
            switch (channel.Id)
            {
                case 0: red = DecodeChannel(data, channel, w, h); break;
                case 1: green = DecodeChannel(data, channel, w, h); break;
                case 2: blue = DecodeChannel(data, channel, w, h); break;
                case -1: alpha = DecodeChannel(data, channel, w, h); break;
            }
        }

        if (w == 0 || h == 0)
        {
            record.Image = PixelImage.Empty;
        }
        else
        {
            var pixels = new byte[w * h * 4];
            int count = w * h;
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                pixels[o] = blue?[i] ?? 0;
                pixels[o + 1] = green?[i] ?? 0;
                pixels[o + 2] = red?[i] ?? 0;
                pixels[o + 3] = alpha?[i] ?? 255;
            }
            var image = new PixelImage(w, h, pixels);
            // キャンバス全体の大きさで保存されたレイヤーも多いため、不透明な範囲に切り詰めて省メモリ・高速化する
            var opaque = image.OpaqueBounds;
            if (opaque.IsEmpty)
            {
                image = PixelImage.Empty;
            }
            else if (opaque.Width != w || opaque.Height != h)
            {
                image = image.Crop(opaque);
                record.ImageOffsetX = opaque.X;
                record.ImageOffsetY = opaque.Y;
            }
            record.Image = image;
        }
        record.Mask = DecodeMask(data, record);
    }

    private static LayerMask? DecodeMask(byte[] data, LayerRecord record)
    {
        if (!record.HasMaskData) return null;
        bool disabled = (record.MaskFlags & 0x02) != 0;
        LayerMask? user = null, real = null;
        foreach (var channel in record.Channels)
        {
            if (channel.Id == -2)
            {
                var b = record.MaskBounds;
                var plane = DecodeChannel(data, channel, Math.Max(0, b.Width), Math.Max(0, b.Height));
                user = new LayerMask(b, plane, record.MaskDefaultColor, disabled);
            }
            else if (channel.Id == -3 && record.RealMaskBounds is { } rb)
            {
                var plane = DecodeChannel(data, channel, Math.Max(0, rb.Width), Math.Max(0, rb.Height));
                real = new LayerMask(rb, plane, record.RealMaskDefaultColor, disabled);
            }
        }
        if (user is null) return real;
        if (real is null) return user;
        return CombineMasks(user, real);
    }

    /// <summary>2 つのマスクを掛け合わせて 1 つにする（ベクトルマスクとレイヤーマスクの併用時）。</summary>
    private static LayerMask CombineMasks(LayerMask a, LayerMask b)
    {
        var bounds = a.Bounds.Union(b.Bounds);
        byte defaultColor = (byte)(a.DefaultColor * b.DefaultColor / 255);
        var plane = new byte[Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height)];
        for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++)
                plane[y * bounds.Width + x] = (byte)(a.Sample(bounds.X + x, bounds.Y + y) * b.Sample(bounds.X + x, bounds.Y + y) / 255);
        return new LayerMask(bounds, plane, defaultColor, a.Disabled);
    }

    private static byte[] DecodeChannel(byte[] data, ChannelInfo channel, int width, int height)
    {
        var plane = new byte[width * height];
        if (channel.Length < 2 || width == 0 || height == 0) return plane;
        long start = channel.DataOffset;
        long end = Math.Min(start + channel.Length, data.Length);
        if (start + 2 > end) return plane;
        int compression = (data[start] << 8) | data[start + 1];
        var body = new ReadOnlySpan<byte>(data, (int)(start + 2), (int)(end - start - 2));
        try
        {
            switch (compression)
            {
                case 0:
                    body[..Math.Min(body.Length, plane.Length)].CopyTo(plane);
                    break;
                case 1:
                    DecodeRle(body, plane, width, height);
                    break;
                case 2:
                    Inflate(body, plane);
                    break;
                case 3:
                    Inflate(body, plane);
                    for (int y = 0; y < height; y++)
                    {
                        int row = y * width;
                        for (int x = 1; x < width; x++)
                            plane[row + x] = (byte)(plane[row + x] + plane[row + x - 1]);
                    }
                    break;
                default:
                    throw new DocumentFormatException($"未対応の圧縮形式です（{compression}）。");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new DocumentFormatException("画像データの展開に失敗しました。ファイルが破損している可能性があります。", ex);
        }
        return plane;
    }

    private static void DecodeRle(ReadOnlySpan<byte> body, byte[] plane, int width, int height)
    {
        int countsSize = height * 2;
        if (body.Length < countsSize) return;
        int offset = countsSize;
        for (int y = 0; y < height; y++)
        {
            int rowLength = (body[y * 2] << 8) | body[y * 2 + 1];
            if (offset >= body.Length) break;
            int available = Math.Min(rowLength, body.Length - offset);
            PackBits.Decode(body.Slice(offset, available), plane.AsSpan(y * width, width));
            offset += rowLength;
        }
    }

    private static void Inflate(ReadOnlySpan<byte> body, byte[] plane)
    {
        using var input = new MemoryStream(body.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        int total = 0;
        while (total < plane.Length)
        {
            int read = zlib.Read(plane, total, plane.Length - total);
            if (read == 0) break;
            total += read;
        }
    }

    private static PixelImage ReadMergedImage(BigEndianReader r, int width, int height, int channelCount)
    {
        if (r.Remaining < 2) return new PixelImage(width, height, new byte[width * height * 4]);
        int compression = r.ReadUInt16();
        int planeSize = width * height;
        int channels = Math.Min(channelCount, 3);
        var planes = new byte[3][];
        for (int c = 0; c < 3; c++) planes[c] = new byte[planeSize];

        if (compression == 0)
        {
            for (int c = 0; c < channels; c++)
            {
                var span = r.ReadSpan((int)Math.Min(planeSize, r.Remaining));
                span.CopyTo(planes[c]);
            }
        }
        else if (compression == 1)
        {
            var counts = new int[channelCount * height];
            for (int i = 0; i < counts.Length; i++) counts[i] = r.ReadUInt16();
            for (int c = 0; c < channels; c++)
            {
                for (int y = 0; y < height; y++)
                {
                    int len = counts[c * height + y];
                    var row = r.ReadSpan((int)Math.Min(len, r.Remaining));
                    PackBits.Decode(row, planes[c].AsSpan(y * width, width));
                }
            }
        }
        else
        {
            throw new DocumentFormatException($"未対応の圧縮形式です（{compression}）。");
        }

        var pixels = new byte[planeSize * 4];
        for (int i = 0; i < planeSize; i++)
        {
            pixels[i * 4] = planes[2][i];
            pixels[i * 4 + 1] = planes[1][i];
            pixels[i * 4 + 2] = planes[0][i];
            pixels[i * 4 + 3] = 255;
        }
        return new PixelImage(width, height, pixels);
    }

    private static void BuildTree(LayerDocument document, List<LayerRecord> records)
    {
        var stack = new Stack<LayerGroup>();
        var current = document.Root;

        // ファイル内は下（背面）から上（前面）の順。先頭に挿入していくと画面の並び順になる
        foreach (var record in records)
        {
            switch (record.SectionType)
            {
                case 3:
                {
                    var group = new LayerGroup(string.Empty);
                    current.Insert(0, group);
                    stack.Push(current);
                    current = group;
                    break;
                }
                case 1:
                case 2:
                {
                    LayerGroup group;
                    if (stack.Count > 0 && !current.IsRoot)
                    {
                        group = current;
                        current = stack.Pop();
                    }
                    else
                    {
                        // 区切りのないフォルダ（壊れたファイル）は空のフォルダとして扱う
                        group = new LayerGroup(string.Empty);
                        current.Insert(0, group);
                    }
                    ApplyCommon(group, record);
                    group.IsExpanded = record.SectionType == 1;
                    var key = record.SectionBlendKey ?? record.BlendKey;
                    group.BlendMode = BlendModeKeys.FromKey(key);
                    break;
                }
                default:
                {
                    var image = record.Image ?? PixelImage.Empty;
                    var layer = image.IsEmpty
                        ? new LayerItem(string.Empty, image, 0, 0)
                        : new LayerItem(string.Empty, image, record.Bounds.X + record.ImageOffsetX, record.Bounds.Y + record.ImageOffsetY);
                    ApplyCommon(layer, record);
                    layer.BlendMode = BlendModeKeys.FromKey(record.BlendKey);
                    current.Insert(0, layer);
                    break;
                }
            }
        }
    }

    private static void ApplyCommon(LayerNode node, LayerRecord record)
    {
        node.InternalName = record.UnicodeName ?? record.LegacyName;
        node.IsVisible = (record.Flags & 0x02) == 0;
        node.Opacity = record.Opacity;
        node.FillOpacity = record.FillOpacity;
        node.Clipping = record.Clipping;
        node.Mask = record.Mask;
    }

    private sealed class ChannelInfo
    {
        public short Id;
        public uint Length;
        public long DataOffset;
    }

    private sealed class LayerRecord
    {
        public PixelRect Bounds;
        public List<ChannelInfo> Channels { get; } = [];
        public string BlendKey = "norm";
        public byte Opacity = 255;
        public byte FillOpacity = 255;
        public bool Clipping;
        public byte Flags;
        public string LegacyName = string.Empty;
        public string? UnicodeName;
        public int SectionType;
        public string? SectionBlendKey;

        public bool HasMaskData;
        public PixelRect MaskBounds;
        public byte MaskDefaultColor;
        public byte MaskFlags;
        public PixelRect? RealMaskBounds;
        public byte RealMaskDefaultColor;

        public PixelImage? Image;
        public int ImageOffsetX;
        public int ImageOffsetY;
        public LayerMask? Mask;
    }
}
