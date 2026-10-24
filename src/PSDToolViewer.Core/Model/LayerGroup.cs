namespace PSDToolViewer.Core.Model;

/// <summary>フォルダ（レイヤーグループ）。<see cref="Children"/> は画面の並び順（先頭が最前面）。</summary>
public sealed class LayerGroup : LayerNode
{
    private readonly List<LayerNode> _children = [];
    private bool _isExpanded = true;

    public LayerGroup(string internalName, bool isRoot = false)
        : base(internalName)
    {
        IsRoot = isRoot;
        BlendMode = BlendMode.PassThrough;
    }

    public bool IsRoot { get; }

    public IReadOnlyList<LayerNode> Children => _children;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public override bool IsGroup => true;

    public int IndexOf(LayerNode node) => _children.IndexOf(node);

    public void Add(LayerNode node) => Insert(_children.Count, node);

    public void Insert(int index, LayerNode node)
    {
        if (ReferenceEquals(node, this) || IsDescendantOf(node))
            throw new InvalidOperationException("フォルダを自身の中に移動することはできません。");
        node.Parent?.Remove(node);
        index = Math.Clamp(index, 0, _children.Count);
        _children.Insert(index, node);
        node.Parent = this;
    }

    public bool Remove(LayerNode node)
    {
        if (!_children.Remove(node)) return false;
        node.Parent = null;
        return true;
    }

    /// <summary>子の並びをまとめて置き換える（元に戻す処理用）。</summary>
    internal void ReplaceChildren(IEnumerable<LayerNode> children)
    {
        foreach (var child in _children)
            if (ReferenceEquals(child.Parent, this)) child.Parent = null;
        _children.Clear();
        foreach (var child in children)
        {
            _children.Add(child);
            child.Parent = this;
        }
    }

    /// <summary>同じ階層のラジオボタンのうち、表示されるものを 1 つだけにする。</summary>
    public void NormalizeRadios()
    {
        LayerNode? selected = null;
        foreach (var child in _children)
        {
            if (!child.IsRadio) continue;
            if (selected is null && child.IsVisible) selected = child;
        }
        if (selected is null)
            selected = _children.FirstOrDefault(c => c.IsRadio);
        foreach (var child in _children)
            if (child.IsRadio) child.IsVisible = ReferenceEquals(child, selected);
    }
}
