using PSDToolViewer.Core.Imaging;

namespace PSDToolViewer.Core.Model;

/// <summary>レイヤー構造に対する編集操作。履歴への記録は呼び出し側で行う。</summary>
public static class DocumentOperations
{
    public const string DefaultGroupName = "新規フォルダ";

    /// <summary>
    /// 選択の中から、他の選択項目の子孫になっているものを除き、画面の並び順で返す。
    /// </summary>
    public static List<LayerNode> TopLevelInOrder(LayerDocument document, IEnumerable<LayerNode> nodes)
    {
        var set = nodes.ToHashSet();
        var result = new List<LayerNode>();
        foreach (var node in document.EnumerateNodes())
        {
            if (!set.Contains(node)) continue;
            if (result.Any(r => r is LayerGroup && node.IsDescendantOf(r))) continue;
            result.Add(node);
        }
        return result;
    }

    /// <summary>表示を切り替える。ラジオボタンは同じ階層の他の項目を非表示にし、強制表示は変更しない。</summary>
    public static bool SetVisible(LayerNode node, bool visible)
    {
        if (node.IsForced) return false;
        if (node.IsRadio)
        {
            if (!visible || node.IsVisible) return false;
            if (node.Parent is { } parent)
                foreach (var sibling in parent.Children)
                    if (sibling.IsRadio && !ReferenceEquals(sibling, node)) sibling.IsVisible = false;
            node.IsVisible = true;
            return true;
        }
        if (node.IsVisible == visible) return false;
        node.IsVisible = visible;
        return true;
    }

    /// <summary>
    /// フォルダを作成する。選択があれば最も上の選択項目のすぐ上（同じ階層）、なければ一番下に追加する。
    /// </summary>
    public static LayerGroup CreateGroup(LayerDocument document, IEnumerable<LayerNode> selection, string name = DefaultGroupName)
    {
        var group = new LayerGroup(name) { IsVisible = true, IsExpanded = true };
        var top = TopLevelInOrder(document, selection).FirstOrDefault();
        if (top?.Parent is { } parent)
            parent.Insert(parent.IndexOf(top), group);
        else
            document.Root.Add(group);
        return group;
    }

    public static void Delete(LayerDocument document, IEnumerable<LayerNode> nodes)
    {
        foreach (var node in TopLevelInOrder(document, nodes))
            node.Parent?.Remove(node);
        document.NormalizeRadios();
    }

    /// <summary>フォルダを自身の中へ移動するような操作でないか。</summary>
    public static bool CanMove(LayerDocument document, IEnumerable<LayerNode> nodes, LayerGroup target)
    {
        var moving = TopLevelInOrder(document, nodes);
        if (moving.Count == 0) return false;
        return moving.All(n => !ReferenceEquals(n, target) && !target.IsDescendantOf(n));
    }

    /// <summary>
    /// 項目を <paramref name="target"/> の <paramref name="index"/> の位置（移動前の並びでの位置）へ移動する。
    /// 実際に並びが変わった場合は true。
    /// </summary>
    public static bool Move(LayerDocument document, IEnumerable<LayerNode> nodes, LayerGroup target, int index)
    {
        var moving = TopLevelInOrder(document, nodes);
        if (moving.Count == 0) return false;
        if (moving.Any(n => ReferenceEquals(n, target) || target.IsDescendantOf(n))) return false;

        // 挿入位置の目印：移動しない項目のうち、指定位置以降で最初のもの
        LayerNode? anchor = null;
        for (int i = Math.Max(0, index); i < target.Children.Count; i++)
        {
            if (!moving.Contains(target.Children[i])) { anchor = target.Children[i]; break; }
        }

        var before = moving.Select(n => (n.Parent, n.Parent?.IndexOf(n) ?? -1)).ToList();

        foreach (var node in moving)
            node.Parent?.Remove(node);

        int insertAt = anchor is null ? target.Children.Count : target.IndexOf(anchor);
        foreach (var node in moving)
            target.Insert(insertAt++, node);

        document.NormalizeRadios();

        var after = moving.Select(n => (n.Parent, n.Parent?.IndexOf(n) ?? -1)).ToList();
        return !before.SequenceEqual(after);
    }

    /// <summary>ラジオボタン化の切替。すべてラジオボタンなら解除、それ以外は全部に付ける。</summary>
    public static bool ToggleRadio(LayerDocument document, IReadOnlyCollection<LayerNode> nodes)
    {
        if (nodes.Count == 0) return false;
        bool remove = nodes.All(n => n.IsRadio);
        foreach (var node in nodes)
        {
            // 強制表示から切り替えた場合は、表示されていた状態を引き継ぐ
            if (node.IsForced) node.IsVisible = true;
            node.InternalName = LayerNaming.Compose(radio: !remove, forced: false, node.DisplayName);
        }
        document.NormalizeRadios();
        return true;
    }

    /// <summary>強制表示の切替。すべて強制表示なら解除、それ以外は全部に付ける。</summary>
    public static bool ToggleForced(LayerDocument document, IReadOnlyCollection<LayerNode> nodes)
    {
        if (nodes.Count == 0) return false;
        bool remove = nodes.All(n => n.IsForced);
        foreach (var node in nodes)
        {
            node.InternalName = LayerNaming.Compose(radio: false, forced: !remove, node.DisplayName);
            // 付けても外しても表示された状態にする（解除時に突然消えないように）
            node.IsVisible = true;
        }
        document.NormalizeRadios();
        return true;
    }

    /// <summary>表示名を変更する。記号は維持し、入力された先頭の記号は取り除く。</summary>
    public static bool Rename(LayerNode node, string displayName)
    {
        displayName = LayerNaming.ToDisplayName(displayName.Trim());
        if (displayName.Length == 0) return false;
        var name = LayerNaming.GetPrefix(node.InternalName) + displayName;
        if (name == node.InternalName) return false;
        node.InternalName = name;
        return true;
    }

    /// <summary>レイヤーを追加し、必要ならキャンバスを広げる。</summary>
    public static void AddLayers(LayerDocument document, IEnumerable<LayerItem> layers, LayerGroup? target = null, int index = int.MaxValue)
    {
        target ??= document.Root;
        int insertAt = Math.Clamp(index, 0, target.Children.Count);
        foreach (var layer in layers)
        {
            target.Insert(insertAt++, layer);
            document.EnsureCanvasContains(new PixelRect(0, 0, layer.Left + layer.Image.Width, layer.Top + layer.Image.Height));
        }
        document.NormalizeRadios();
    }
}
