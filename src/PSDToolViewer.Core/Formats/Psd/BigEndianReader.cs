using System.Buffers.Binary;
using System.Text;

namespace PSDToolViewer.Core.Formats.Psd;

/// <summary>メモリ上の PSD データをビッグエンディアンで読む。</summary>
internal sealed class BigEndianReader(byte[] data)
{
    public byte[] Data { get; } = data;
    public long Position { get; set; }
    public long Length => Data.Length;
    public long Remaining => Data.Length - Position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (Position < 0 || Position + count > Data.Length)
            throw new DocumentFormatException("ファイルが途中で終わっています。破損している可能性があります。");
        var span = new ReadOnlySpan<byte>(Data, (int)Position, count);
        Position += count;
        return span;
    }

    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSByte() => (sbyte)Take(1)[0];
    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    public byte[] ReadBytes(int count) => Take(count).ToArray();
    public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);
    public string ReadAscii(int count) => Encoding.ASCII.GetString(Take(count));
    public void Skip(long count) => Seek(Position + count);

    public void Seek(long position)
    {
        if (position < 0 || position > Data.Length)
            throw new DocumentFormatException("ファイルが途中で終わっています。破損している可能性があります。");
        Position = position;
    }

    /// <summary>指定位置の 4 文字を読まずに確認する。</summary>
    public bool PeekAscii(long position, string text)
    {
        if (position < 0 || position + text.Length > Data.Length) return false;
        for (int i = 0; i < text.Length; i++)
            if (Data[position + i] != text[i]) return false;
        return true;
    }
}
