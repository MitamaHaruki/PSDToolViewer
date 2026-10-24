namespace PSDToolViewer.Core.Formats.Psd;

/// <summary>PackBits（PSD の RLE 圧縮）。</summary>
internal static class PackBits
{
    /// <summary>展開して <paramref name="destination"/> を埋める。読んだバイト数を返す。</summary>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int si = 0, di = 0;
        while (di < destination.Length && si < source.Length)
        {
            int n = (sbyte)source[si++];
            if (n >= 0)
            {
                int count = Math.Min(n + 1, Math.Min(destination.Length - di, source.Length - si));
                source.Slice(si, count).CopyTo(destination[di..]);
                si += n + 1;
                di += count;
            }
            else if (n != -128)
            {
                if (si >= source.Length) break;
                int count = Math.Min(1 - n, destination.Length - di);
                destination.Slice(di, count).Fill(source[si++]);
                di += count;
            }
        }
        return Math.Min(si, source.Length);
    }

    /// <summary>1 行分を圧縮して <paramref name="output"/> に追加し、書いたバイト数を返す。</summary>
    public static int Encode(ReadOnlySpan<byte> source, MemoryStream output)
    {
        long start = output.Position;
        int n = source.Length;
        int i = 0;
        while (i < n)
        {
            int run = 1;
            while (i + run < n && run < 128 && source[i + run] == source[i]) run++;
            if (run >= 3 || (run == 2 && i + run == n))
            {
                output.WriteByte((byte)(sbyte)(1 - run));
                output.WriteByte(source[i]);
                i += run;
                continue;
            }

            int j = i;
            while (j < n && j - i < 128)
            {
                if (j + 2 < n && source[j] == source[j + 1] && source[j] == source[j + 2]) break;
                j++;
            }
            output.WriteByte((byte)(j - i - 1));
            output.Write(source[i..j]);
            i = j;
        }
        return (int)(output.Position - start);
    }
}
