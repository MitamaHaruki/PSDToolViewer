namespace PSDToolViewer.Core.Model;

public enum BlendMode
{
    PassThrough,
    Normal,
    Dissolve,
    Darken,
    Multiply,
    ColorBurn,
    LinearBurn,
    DarkerColor,
    Lighten,
    Screen,
    ColorDodge,
    LinearDodge,
    LighterColor,
    Overlay,
    SoftLight,
    HardLight,
    VividLight,
    LinearLight,
    PinLight,
    HardMix,
    Difference,
    Exclusion,
    Subtract,
    Divide,
    Hue,
    Saturation,
    Color,
    Luminosity,
}

public static class BlendModeKeys
{
    private static readonly (string Key, BlendMode Mode)[] Map =
    [
        ("pass", BlendMode.PassThrough),
        ("norm", BlendMode.Normal),
        ("diss", BlendMode.Dissolve),
        ("dark", BlendMode.Darken),
        ("mul ", BlendMode.Multiply),
        ("idiv", BlendMode.ColorBurn),
        ("lbrn", BlendMode.LinearBurn),
        ("dkCl", BlendMode.DarkerColor),
        ("lite", BlendMode.Lighten),
        ("scrn", BlendMode.Screen),
        ("div ", BlendMode.ColorDodge),
        ("lddg", BlendMode.LinearDodge),
        ("lgCl", BlendMode.LighterColor),
        ("over", BlendMode.Overlay),
        ("sLit", BlendMode.SoftLight),
        ("hLit", BlendMode.HardLight),
        ("vLit", BlendMode.VividLight),
        ("lLit", BlendMode.LinearLight),
        ("pLit", BlendMode.PinLight),
        ("hMix", BlendMode.HardMix),
        ("diff", BlendMode.Difference),
        ("smud", BlendMode.Exclusion),
        ("fsub", BlendMode.Subtract),
        ("fdiv", BlendMode.Divide),
        ("hue ", BlendMode.Hue),
        ("sat ", BlendMode.Saturation),
        ("colr", BlendMode.Color),
        ("lum ", BlendMode.Luminosity),
    ];

    public static BlendMode FromKey(string key)
    {
        foreach (var (k, m) in Map)
            if (k == key) return m;
        return BlendMode.Normal;
    }

    public static string ToKey(BlendMode mode)
    {
        foreach (var (k, m) in Map)
            if (m == mode) return k;
        return "norm";
    }

    public static bool TryParse(string key, out BlendMode mode)
    {
        foreach (var (k, m) in Map)
            if (k == key) { mode = m; return true; }
        mode = BlendMode.Normal;
        return false;
    }
}
