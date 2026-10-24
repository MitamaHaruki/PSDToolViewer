using System.Text;

namespace PSDToolViewer.Core.Formats.Psd;

/// <summary>PSD の旧形式レイヤー名（Unicode 名がない場合に使われる）の文字コード。日本語環境に合わせて Shift_JIS を使う。</summary>
internal static class LegacyEncoding
{
    private static readonly Lazy<Encoding> StrictShiftJis = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    });

    private static readonly Lazy<Encoding> LenientShiftJis = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    });

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictShiftJis.Value.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>最大 <paramref name="maxBytes"/> バイトに収まるように変換する（文字の途中で切らない）。</summary>
    public static byte[] Encode(string text, int maxBytes)
    {
        var encoding = LenientShiftJis.Value;
        var bytes = encoding.GetBytes(text);
        if (bytes.Length <= maxBytes) return bytes;
        int chars = text.Length;
        while (chars > 0)
        {
            chars--;
            bytes = encoding.GetBytes(text[..chars]);
            if (bytes.Length <= maxBytes) return bytes;
        }
        return [];
    }
}
