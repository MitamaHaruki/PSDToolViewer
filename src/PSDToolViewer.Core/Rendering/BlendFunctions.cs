using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Rendering;

/// <summary>描画モードの計算。チャンネルごとに独立したモードは 256×256 の表を使う。</summary>
internal static class BlendFunctions
{
    private static readonly byte[]?[] Tables = new byte[Enum.GetValues<BlendMode>().Length][];
    private static readonly object Gate = new();

    public static bool IsNormal(BlendMode mode) => mode is BlendMode.Normal or BlendMode.Dissolve or BlendMode.PassThrough;

    public static bool IsNonSeparable(BlendMode mode)
        => mode is BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity
            or BlendMode.DarkerColor or BlendMode.LighterColor;

    /// <summary>チャンネルごとの表（添字は <c>(背景 &lt;&lt; 8) | 前景</c>）。</summary>
    public static byte[] GetTable(BlendMode mode)
    {
        var table = Tables[(int)mode];
        if (table is not null) return table;
        lock (Gate)
        {
            table = Tables[(int)mode];
            if (table is not null) return table;
            table = new byte[65536];
            for (int b = 0; b < 256; b++)
                for (int s = 0; s < 256; s++)
                    table[(b << 8) | s] = (byte)Math.Clamp(Math.Round(Separable(mode, b / 255.0, s / 255.0) * 255.0), 0, 255);
            Tables[(int)mode] = table;
            return table;
        }
    }

    private static double Separable(BlendMode mode, double cb, double cs) => mode switch
    {
        BlendMode.Multiply => cb * cs,
        BlendMode.Screen => cb + cs - cb * cs,
        BlendMode.Overlay => HardLight(cs, cb),
        BlendMode.Darken => Math.Min(cb, cs),
        BlendMode.Lighten => Math.Max(cb, cs),
        BlendMode.ColorDodge => ColorDodge(cb, cs),
        BlendMode.ColorBurn => ColorBurn(cb, cs),
        BlendMode.LinearBurn => Math.Max(0, cb + cs - 1),
        BlendMode.LinearDodge => Math.Min(1, cb + cs),
        BlendMode.HardLight => HardLight(cb, cs),
        BlendMode.SoftLight => cs <= 0.5
            ? 2 * cb * cs + cb * cb * (1 - 2 * cs)
            : Math.Sqrt(cb) * (2 * cs - 1) + 2 * cb * (1 - cs),
        BlendMode.Difference => Math.Abs(cb - cs),
        BlendMode.Exclusion => cb + cs - 2 * cb * cs,
        BlendMode.Subtract => Math.Max(0, cb - cs),
        BlendMode.Divide => cs <= 0 ? (cb <= 0 ? 0 : 1) : Math.Min(1, cb / cs),
        BlendMode.VividLight => cs <= 0.5 ? ColorBurn(cb, 2 * cs) : ColorDodge(cb, 2 * (cs - 0.5)),
        BlendMode.LinearLight => Math.Clamp(cb + 2 * cs - 1, 0, 1),
        BlendMode.PinLight => cs <= 0.5 ? Math.Min(cb, 2 * cs) : Math.Max(cb, 2 * cs - 1),
        BlendMode.HardMix => cb + cs >= 1 ? 1 : 0,
        _ => cs,
    };

    private static double HardLight(double cb, double cs)
        => cs <= 0.5 ? cb * 2 * cs : cb + (2 * cs - 1) - cb * (2 * cs - 1);

    private static double ColorDodge(double cb, double cs)
    {
        if (cb <= 0) return 0;
        if (cs >= 1) return 1;
        return Math.Min(1, cb / (1 - cs));
    }

    private static double ColorBurn(double cb, double cs)
    {
        if (cb >= 1) return 1;
        if (cs <= 0) return 0;
        return 1 - Math.Min(1, (1 - cb) / cs);
    }

    /// <summary>色相・彩度・カラー・輝度・カラー比較（暗/明）。値は 0～255。</summary>
    public static void NonSeparable(BlendMode mode, int bb, int bg, int br, int sb, int sg, int sr, out int ob, out int og, out int or)
    {
        float cbB = bb / 255f, cbG = bg / 255f, cbR = br / 255f;
        float csB = sb / 255f, csG = sg / 255f, csR = sr / 255f;
        float rB, rG, rR;
        switch (mode)
        {
            case BlendMode.Hue:
                SetSat(csR, csG, csB, Sat(cbR, cbG, cbB), out var hR, out var hG, out var hB);
                SetLum(hR, hG, hB, Lum(cbR, cbG, cbB), out rR, out rG, out rB);
                break;
            case BlendMode.Saturation:
                SetSat(cbR, cbG, cbB, Sat(csR, csG, csB), out var sR, out var sG, out var sB2);
                SetLum(sR, sG, sB2, Lum(cbR, cbG, cbB), out rR, out rG, out rB);
                break;
            case BlendMode.Color:
                SetLum(csR, csG, csB, Lum(cbR, cbG, cbB), out rR, out rG, out rB);
                break;
            case BlendMode.Luminosity:
                SetLum(cbR, cbG, cbB, Lum(csR, csG, csB), out rR, out rG, out rB);
                break;
            case BlendMode.DarkerColor:
                (rR, rG, rB) = Lum(csR, csG, csB) < Lum(cbR, cbG, cbB) ? (csR, csG, csB) : (cbR, cbG, cbB);
                break;
            case BlendMode.LighterColor:
                (rR, rG, rB) = Lum(csR, csG, csB) > Lum(cbR, cbG, cbB) ? (csR, csG, csB) : (cbR, cbG, cbB);
                break;
            default:
                (rR, rG, rB) = (csR, csG, csB);
                break;
        }
        ob = ToByte(rB);
        og = ToByte(rG);
        or = ToByte(rR);
    }

    private static int ToByte(float v) => (int)Math.Clamp(MathF.Round(v * 255f), 0, 255);

    private static float Lum(float r, float g, float b) => 0.3f * r + 0.59f * g + 0.11f * b;

    private static float Sat(float r, float g, float b) => MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));

    private static void SetLum(float r, float g, float b, float l, out float or, out float og, out float ob)
    {
        float d = l - Lum(r, g, b);
        r += d; g += d; b += d;
        float lum = Lum(r, g, b);
        float n = MathF.Min(r, MathF.Min(g, b));
        float x = MathF.Max(r, MathF.Max(g, b));
        if (n < 0)
        {
            float k = lum - n;
            if (k > 0)
            {
                r = lum + (r - lum) * lum / k;
                g = lum + (g - lum) * lum / k;
                b = lum + (b - lum) * lum / k;
            }
        }
        if (x > 1)
        {
            float k = x - lum;
            if (k > 0)
            {
                r = lum + (r - lum) * (1 - lum) / k;
                g = lum + (g - lum) * (1 - lum) / k;
                b = lum + (b - lum) * (1 - lum) / k;
            }
        }
        or = r; og = g; ob = b;
    }

    private static void SetSat(float r, float g, float b, float s, out float or, out float og, out float ob)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float range = max - min;
        if (range <= 0)
        {
            or = og = ob = 0;
            return;
        }
        or = (r - min) * s / range;
        og = (g - min) * s / range;
        ob = (b - min) * s / range;
    }
}
