using System.Text;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Formats.Psd;

/// <summary>PSD ファイルの書き出し（8bit RGB、RLE 圧縮）。レイヤー名には内部名を使う。</summary>
public static class PsdWriter
{
    private const string GroupDividerName = "</Layer group>";

    /// <param name="composite">統合画像。出力するキャンバスと同じサイズであること。</param>
    /// <param name="canvasRect">出力するキャンバスの範囲（ドキュメント座標）。null ならキャンバス全体。</param>
    public static void Write(
        LayerDocument document,
        Stream output,
        PixelImage composite,
        PixelRect? canvasRect = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var canvas = canvasRect ?? new PixelRect(0, 0, document.Width, document.Height);
        if (canvas.IsEmpty) throw new InvalidOperationException("キャンバスが空のため保存できません。");
        if (composite.Width != canvas.Width || composite.Height != canvas.Height)
            throw new ArgumentException("統合画像のサイズがキャンバスと一致しません。", nameof(composite));

        var entries = new List<Entry>();
        Flatten(document.Root, entries, -canvas.X, -canvas.Y);

        int done = 0;
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.ForEach(entries, options, entry =>
        {
            entry.EncodeChannels();
            int n = Interlocked.Increment(ref done);
            progress?.Report(0.85 * n / Math.Max(1, entries.Count));
        });
        cancellationToken.ThrowIfCancellationRequested();

        var w = new BigEndianWriter(output);

        // ヘッダー
        w.WriteAscii("8BPS");
        w.WriteUInt16(1);
        w.WriteZeros(6);
        w.WriteUInt16(4);
        w.WriteInt32(canvas.Height);
        w.WriteInt32(canvas.Width);
        w.WriteUInt16(8);
        w.WriteUInt16(3);

        // カラーモードデータ
        w.WriteUInt32(0);

        // 画像リソース
        long resourcesPos = w.BeginLength();
        foreach (var resource in document.ImageResources)
        {
            w.WriteAscii("8BIM");
            w.WriteUInt16(resource.Id);
            var nameBytes = LegacyEncoding.Encode(resource.Name, 255);
            w.WriteByte((byte)nameBytes.Length);
            w.WriteBytes(nameBytes);
            if ((nameBytes.Length + 1) % 2 != 0) w.WriteByte(0);
            w.WriteUInt32((uint)resource.Data.Length);
            w.WriteBytes(resource.Data);
            if (resource.Data.Length % 2 != 0) w.WriteByte(0);
        }
        w.EndLength(resourcesPos);

        // レイヤーとマスク情報
        long layerSectionPos = w.BeginLength();
        long layerInfoPos = w.BeginLength();
        if (entries.Count > 0)
        {
            // 負の数：統合画像の最初のアルファチャンネルが透明度を表す
            w.WriteInt16((short)-entries.Count);
            foreach (var entry in entries) WriteRecord(w, entry);
            foreach (var entry in entries)
                foreach (var (_, data) in entry.Channels)
                    w.WriteBytes(data);
        }
        w.EndLength(layerInfoPos, entries.Count > 0 ? 4 : 1);
        w.WriteUInt32(0); // グローバルレイヤーマスク
        w.EndLength(layerSectionPos);

        cancellationToken.ThrowIfCancellationRequested();
        WriteComposite(w, composite);
        progress?.Report(1);
    }

    private static void Flatten(LayerGroup group, List<Entry> entries, int dx, int dy)
    {
        // ファイル内は下（背面）から上（前面）の順
        for (int i = group.Children.Count - 1; i >= 0; i--)
        {
            var node = group.Children[i];
            if (node is LayerGroup child)
            {
                entries.Add(new Entry
                {
                    Name = GroupDividerName,
                    SectionType = 3,
                    Flags = 0x18,
                });
                Flatten(child, entries, dx, dy);
                entries.Add(new Entry
                {
                    Name = child.InternalName,
                    SectionType = child.IsExpanded ? 1 : 2,
                    BlendMode = child.BlendMode,
                    Opacity = child.Opacity,
                    FillOpacity = child.FillOpacity,
                    Clipping = child.Clipping,
                    Flags = (byte)(0x18 | (child.IsShown ? 0 : 0x02)),
                    Mask = child.Mask?.Offset(dx, dy),
                });
            }
            else if (node is LayerItem layer)
            {
                entries.Add(new Entry
                {
                    Name = layer.InternalName,
                    Bounds = layer.Bounds.Offset(dx, dy),
                    Image = layer.Image,
                    BlendMode = layer.BlendMode == BlendMode.PassThrough ? BlendMode.Normal : layer.BlendMode,
                    Opacity = layer.Opacity,
                    FillOpacity = layer.FillOpacity,
                    Clipping = layer.Clipping,
                    Flags = (byte)(0x08 | (layer.IsShown ? 0 : 0x02)),
                    Mask = layer.Mask?.Offset(dx, dy),
                });
            }
        }
    }

    private static void WriteRecord(BigEndianWriter w, Entry e)
    {
        var b = e.Bounds;
        w.WriteInt32(b.IsEmpty ? 0 : b.Y);
        w.WriteInt32(b.IsEmpty ? 0 : b.X);
        w.WriteInt32(b.IsEmpty ? 0 : b.Bottom);
        w.WriteInt32(b.IsEmpty ? 0 : b.Right);

        w.WriteUInt16((ushort)e.Channels.Count);
        foreach (var (id, data) in e.Channels)
        {
            w.WriteInt16(id);
            w.WriteUInt32((uint)data.Length);
        }

        w.WriteAscii("8BIM");
        w.WriteAscii(BlendModeKeys.ToKey(e.BlendMode));
        w.WriteByte(e.Opacity);
        w.WriteByte((byte)(e.Clipping ? 1 : 0));
        w.WriteByte(e.Flags);
        w.WriteByte(0);

        long extraPos = w.BeginLength();

        // レイヤーマスク
        if (e.Mask is { } mask)
        {
            w.WriteUInt32(20);
            w.WriteInt32(mask.Bounds.Y);
            w.WriteInt32(mask.Bounds.X);
            w.WriteInt32(mask.Bounds.Bottom);
            w.WriteInt32(mask.Bounds.Right);
            w.WriteByte(mask.DefaultColor);
            w.WriteByte((byte)(mask.Disabled ? 0x02 : 0));
            w.WriteZeros(2);
        }
        else
        {
            w.WriteUInt32(0);
        }

        // ブレンド条件（既定値）
        w.WriteUInt32(40);
        for (int i = 0; i < 10; i++)
        {
            w.WriteUInt16(0x0000);
            w.WriteUInt16(0xFFFF);
        }

        // 旧形式の名前
        var nameBytes = LegacyEncoding.Encode(e.Name, 255);
        w.WriteByte((byte)nameBytes.Length);
        w.WriteBytes(nameBytes);
        int written = nameBytes.Length + 1;
        while (written % 4 != 0) { w.WriteByte(0); written++; }

        // Unicode の名前
        WriteBlock(w, "luni", () =>
        {
            w.WriteUInt32((uint)e.Name.Length);
            w.WriteBytes(Encoding.BigEndianUnicode.GetBytes(e.Name));
        });

        if (e.SectionType != 0)
        {
            WriteBlock(w, "lsct", () =>
            {
                w.WriteUInt32((uint)e.SectionType);
                if (e.SectionType != 3)
                {
                    w.WriteAscii("8BIM");
                    w.WriteAscii(BlendModeKeys.ToKey(e.BlendMode));
                }
            });
        }

        if (e.FillOpacity != 255)
            WriteBlock(w, "iOpa", () => w.WriteByte(e.FillOpacity));

        w.EndLength(extraPos);
    }

    private static void WriteBlock(BigEndianWriter w, string key, Action body)
    {
        w.WriteAscii("8BIM");
        w.WriteAscii(key);
        long pos = w.BeginLength();
        body();
        w.EndLength(pos, 4);
    }

    private static void WriteComposite(BigEndianWriter w, PixelImage composite)
    {
        int width = composite.Width, height = composite.Height;
        var src = composite.Data;

        // 透明部分は白と合成した色で保存する（Photoshop と同じ扱い）
        var matted = new byte[src.Length];
        Parallel.For(0, height, y =>
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;
                int a = src[o + 3];
                int inv = 255 - a;
                matted[o] = (byte)((src[o] * a + 255 * inv + 127) / 255);
                matted[o + 1] = (byte)((src[o + 1] * a + 255 * inv + 127) / 255);
                matted[o + 2] = (byte)((src[o + 2] * a + 255 * inv + 127) / 255);
                matted[o + 3] = (byte)a;
            }
        });

        // R, G, B, A の順
        int[] offsets = [2, 1, 0, 3];
        var encoded = new (ushort[] Counts, byte[] Data)[4];
        Parallel.For(0, 4, c => encoded[c] = EncodeRows(matted, width, height, 4, offsets[c]));

        w.WriteUInt16(1);
        foreach (var (counts, _) in encoded)
            foreach (var count in counts) w.WriteUInt16(count);
        foreach (var (_, data) in encoded)
            w.WriteBytes(data);
    }

    private static (ushort[] Counts, byte[] Data) EncodeRows(byte[] src, int width, int height, int pixelSize, int offset)
    {
        var counts = new ushort[height];
        using var ms = new MemoryStream();
        var row = new byte[width];
        for (int y = 0; y < height; y++)
        {
            int start = y * width * pixelSize + offset;
            for (int x = 0; x < width; x++) row[x] = src[start + x * pixelSize];
            counts[y] = checked((ushort)PackBits.Encode(row, ms));
        }
        return (counts, ms.ToArray());
    }

    /// <summary>1 チャンネル分のデータ（圧縮方式 2 バイトを含む）。</summary>
    private static byte[] EncodeChannel(byte[] src, int width, int height, int pixelSize, int offset)
    {
        if (width <= 0 || height <= 0) return [0, 0];
        var (counts, data) = EncodeRows(src, width, height, pixelSize, offset);
        var result = new byte[2 + height * 2 + data.Length];
        result[1] = 1;
        for (int y = 0; y < height; y++)
        {
            result[2 + y * 2] = (byte)(counts[y] >> 8);
            result[3 + y * 2] = (byte)counts[y];
        }
        data.CopyTo(result, 2 + height * 2);
        return result;
    }

    private sealed class Entry
    {
        public string Name = string.Empty;
        public PixelRect Bounds = PixelRect.Empty;
        public PixelImage? Image;
        public BlendMode BlendMode = BlendMode.Normal;
        public byte Opacity = 255;
        public byte FillOpacity = 255;
        public bool Clipping;
        public byte Flags;
        public int SectionType;
        public LayerMask? Mask;
        public List<(short Id, byte[] Data)> Channels { get; } = [];

        public void EncodeChannels()
        {
            if (Image is { IsEmpty: false } image)
            {
                Channels.Add((-1, EncodeChannel(image.Data, image.Width, image.Height, 4, 3)));
                Channels.Add((0, EncodeChannel(image.Data, image.Width, image.Height, 4, 2)));
                Channels.Add((1, EncodeChannel(image.Data, image.Width, image.Height, 4, 1)));
                Channels.Add((2, EncodeChannel(image.Data, image.Width, image.Height, 4, 0)));
            }
            else
            {
                for (short id = -1; id <= 2; id++) Channels.Add((id, [0, 0]));
            }

            if (Mask is { } mask)
                Channels.Add((-2, EncodeChannel(mask.Data, mask.Bounds.Width, mask.Bounds.Height, 1, 0)));
        }
    }
}
