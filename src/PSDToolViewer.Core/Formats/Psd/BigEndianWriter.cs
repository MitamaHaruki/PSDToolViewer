using System.Buffers.Binary;
using System.Text;

namespace PSDToolViewer.Core.Formats.Psd;

/// <summary>ビッグエンディアンで書き込む。長さを後から埋めるため、シーク可能なストリームが必要。</summary>
internal sealed class BigEndianWriter(Stream stream)
{
    private readonly byte[] _buffer = new byte[8];

    public Stream Stream { get; } = stream;
    public long Position => Stream.Position;

    public void WriteByte(byte value) => Stream.WriteByte(value);
    public void WriteSByte(sbyte value) => Stream.WriteByte((byte)value);

    public void WriteInt16(short value)
    {
        BinaryPrimitives.WriteInt16BigEndian(_buffer, value);
        Stream.Write(_buffer, 0, 2);
    }

    public void WriteUInt16(ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer, value);
        Stream.Write(_buffer, 0, 2);
    }

    public void WriteInt32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_buffer, value);
        Stream.Write(_buffer, 0, 4);
    }

    public void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer, value);
        Stream.Write(_buffer, 0, 4);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => Stream.Write(bytes);

    public void WriteAscii(string text) => Stream.Write(Encoding.ASCII.GetBytes(text));

    public void WriteZeros(int count)
    {
        for (int i = 0; i < count; i++) Stream.WriteByte(0);
    }

    /// <summary>4 バイトの長さ欄を確保し、その位置を返す。</summary>
    public long BeginLength()
    {
        long pos = Position;
        WriteUInt32(0);
        return pos;
    }

    /// <summary>
    /// 長さ欄を埋める。<paramref name="padding"/> の倍数になるよう 0 を追加し、追加分も長さに含める。
    /// </summary>
    public void EndLength(long lengthPosition, int padding = 1)
    {
        long length = Position - lengthPosition - 4;
        while (padding > 1 && length % padding != 0)
        {
            Stream.WriteByte(0);
            length++;
        }
        long end = Position;
        Stream.Position = lengthPosition;
        WriteUInt32(checked((uint)length));
        Stream.Position = end;
    }
}
