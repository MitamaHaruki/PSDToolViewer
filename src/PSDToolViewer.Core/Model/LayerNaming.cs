namespace PSDToolViewer.Core.Model;

/// <summary>
/// PSDTool の命名規則。内部名の先頭に付く記号（<c>*</c>：ラジオボタン、<c>!</c>：強制表示）を扱う。
/// </summary>
public static class LayerNaming
{
    public const char RadioMark = '*';
    public const char ForcedMark = '!';

    /// <summary>先頭に連続する記号部分の長さ。</summary>
    public static int PrefixLength(string name)
    {
        int i = 0;
        while (i < name.Length && (name[i] == RadioMark || name[i] == ForcedMark)) i++;
        return i;
    }

    public static string GetPrefix(string name) => name[..PrefixLength(name)];

    /// <summary>表示名（記号を除いた名前）。</summary>
    public static string ToDisplayName(string name) => name[PrefixLength(name)..];

    public static bool HasForcedMark(string name) => GetPrefix(name).Contains(ForcedMark);

    /// <summary>ラジオボタン扱いか。強制表示の記号もある場合は強制表示を優先する。</summary>
    public static bool HasRadioMark(string name)
    {
        var prefix = GetPrefix(name);
        return prefix.Contains(RadioMark) && !prefix.Contains(ForcedMark);
    }

    /// <summary>記号の状態と表示名から内部名を組み立てる。<c>*</c> と <c>!</c> は同時に付けない。</summary>
    public static string Compose(bool radio, bool forced, string displayName)
    {
        displayName = ToDisplayName(displayName);
        if (forced) return ForcedMark + displayName;
        if (radio) return RadioMark + displayName;
        return displayName;
    }
}
