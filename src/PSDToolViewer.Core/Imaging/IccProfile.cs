using System.Buffers.Binary;

namespace PSDToolViewer.Core.Imaging;

/// <summary>
/// 行列とトーンカーブで表される RGB の ICC プロファイル（sRGB・Display P3・Adobe RGB など）。
/// それ以外の形式（LUT 型など）は <see cref="TryParse"/> が false を返す。
/// </summary>
internal sealed class IccProfile
{
    private IccProfile(double[,] toXyz, Func<double, double>[] curves)
    {
        ToXyz = toXyz;
        Curves = curves;
    }

    /// <summary>線形 RGB → XYZ（D50）の行列。</summary>
    public double[,] ToXyz { get; }

    /// <summary>R・G・B の各チャンネルの値（0～1）を線形に戻す関数。</summary>
    public Func<double, double>[] Curves { get; }

    /// <summary>sRGB（IEC 61966-2.1 の ICC と同じ D50 の値）。</summary>
    public static IccProfile Srgb { get; } = new(
        new[,]
        {
            { 0.4360747, 0.3850649, 0.1430804 },
            { 0.2225045, 0.7168786, 0.0606169 },
            { 0.0139322, 0.0971045, 0.7141733 },
        },
        [SrgbDecode, SrgbDecode, SrgbDecode]);

    public static double SrgbDecode(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    public static bool TryParse(byte[] data, out IccProfile profile)
    {
        profile = null!;
        try
        {
            if (data.Length < 132) return false;
            var span = data.AsSpan();
            if (BinaryPrimitives.ReadUInt32BigEndian(span[16..]) != 0x52474220) return false; // 'RGB '
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(span[128..]);
            var tags = new Dictionary<string, (int Offset, int Size)>();
            for (int i = 0; i < count; i++)
            {
                int entry = 132 + i * 12;
                if (entry + 12 > data.Length) return false;
                string sig = System.Text.Encoding.ASCII.GetString(data, entry, 4);
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 4)..]);
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(entry + 8)..]);
                if (offset < 0 || size < 0 || offset + size > data.Length) return false;
                tags[sig] = (offset, size);
            }

            if (!TryReadXyz(data, tags, "rXYZ", out var r) || !TryReadXyz(data, tags, "gXYZ", out var g) || !TryReadXyz(data, tags, "bXYZ", out var b))
                return false;
            if (!TryReadCurve(data, tags, "rTRC", out var rc) || !TryReadCurve(data, tags, "gTRC", out var gc) || !TryReadCurve(data, tags, "bTRC", out var bc))
                return false;

            var matrix = new double[,]
            {
                { r[0], g[0], b[0] },
                { r[1], g[1], b[1] },
                { r[2], g[2], b[2] },
            };
            profile = new IccProfile(matrix, [rc, gc, bc]);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static double S15Fixed16(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadInt32BigEndian(s) / 65536.0;

    private static bool TryReadXyz(byte[] data, Dictionary<string, (int Offset, int Size)> tags, string name, out double[] xyz)
    {
        xyz = [];
        if (!tags.TryGetValue(name, out var tag) || tag.Size < 20) return false;
        var s = data.AsSpan(tag.Offset, tag.Size);
        if (System.Text.Encoding.ASCII.GetString(s[..4]) != "XYZ ") return false;
        xyz = [S15Fixed16(s[8..]), S15Fixed16(s[12..]), S15Fixed16(s[16..])];
        return true;
    }

    private static bool TryReadCurve(byte[] data, Dictionary<string, (int Offset, int Size)> tags, string name, out Func<double, double> curve)
    {
        curve = x => x;
        if (!tags.TryGetValue(name, out var tag) || tag.Size < 12) return false;
        var s = data.AsSpan(tag.Offset, tag.Size);
        string type = System.Text.Encoding.ASCII.GetString(s[..4]);
        if (type == "curv")
        {
            int n = (int)BinaryPrimitives.ReadUInt32BigEndian(s[8..]);
            if (n == 0) return true;
            if (n == 1)
            {
                double gamma = BinaryPrimitives.ReadUInt16BigEndian(s[12..]) / 256.0;
                curve = x => Math.Pow(x, gamma);
                return true;
            }
            if (12 + n * 2 > s.Length) return false;
            var table = new double[n];
            for (int i = 0; i < n; i++) table[i] = BinaryPrimitives.ReadUInt16BigEndian(s[(12 + i * 2)..]) / 65535.0;
            curve = x =>
            {
                double f = Math.Clamp(x, 0, 1) * (n - 1);
                int i = Math.Min(n - 2, (int)f);
                return table[i] + (table[i + 1] - table[i]) * (f - i);
            };
            return true;
        }
        if (type == "para")
        {
            int function = BinaryPrimitives.ReadUInt16BigEndian(s[8..]);
            int[] counts = [1, 3, 4, 5, 7];
            if (function >= counts.Length || 12 + counts[function] * 4 > s.Length) return false;
            var p = new double[7];
            for (int i = 0; i < counts[function]; i++) p[i] = S15Fixed16(s[(12 + i * 4)..]);
            double gm = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
            curve = function switch
            {
                0 => x => Math.Pow(x, gm),
                1 => x => x >= -b / a ? Math.Pow(a * x + b, gm) : 0,
                2 => x => x >= -b / a ? Math.Pow(a * x + b, gm) + c : c,
                3 => x => x >= d ? Math.Pow(a * x + b, gm) : c * x,
                _ => x => x >= d ? Math.Pow(a * x + b, gm) + e : c * x + f,
            };
            return true;
        }
        return false;
    }

    public static double[,] Multiply(double[,] a, double[,] b)
    {
        var m = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return m;
    }

    public static double[,] Invert(double[,] m)
    {
        double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                   - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                   + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        if (Math.Abs(det) < 1e-12) throw new ArgumentException("行列が逆行列を持ちません。");
        double k = 1 / det;
        return new[,]
        {
            { (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) * k, (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) * k, (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) * k },
            { (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) * k, (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) * k, (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) * k },
            { (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) * k, (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) * k, (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) * k },
        };
    }
}
