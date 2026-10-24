namespace PSDToolViewer.Core.Model;

/// <summary>
/// 構造の変更（追加・削除・移動・名前・記号・キャンバスサイズ）の履歴。
/// 表示の切替は履歴に含めない。
/// </summary>
public sealed class EditHistory
{
    private const int MaxEntries = 100;

    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private long _nextStateId = 1;

    /// <summary>現在の状態を表す ID。保存時の ID と比べて未保存かを判定する。</summary>
    public long StateId { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// 変更を実行し、実際に変更があった（<paramref name="change"/> が true を返した）場合だけ履歴に積む。
    /// </summary>
    public bool Apply(LayerDocument document, Func<bool> change)
    {
        var snapshot = DocumentSnapshot.Capture(document);
        if (!change()) return false;
        _undo.Add(new Entry(snapshot, StateId));
        if (_undo.Count > MaxEntries) _undo.RemoveAt(0);
        _redo.Clear();
        StateId = _nextStateId++;
        return true;
    }

    public bool Undo(LayerDocument document) => Step(document, _undo, _redo);

    public bool Redo(LayerDocument document) => Step(document, _redo, _undo);

    private bool Step(LayerDocument document, List<Entry> from, List<Entry> to)
    {
        if (from.Count == 0) return false;
        to.Add(new Entry(DocumentSnapshot.Capture(document), StateId));
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);
        entry.Snapshot.Restore(document);
        StateId = entry.StateId;
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private sealed record Entry(DocumentSnapshot Snapshot, long StateId);
}

/// <summary>ドキュメントの構造のスナップショット。画像は共有し、並びと名前だけを記録する。</summary>
internal sealed class DocumentSnapshot
{
    private readonly int _width;
    private readonly int _height;
    private readonly List<(LayerGroup Group, LayerNode[] Children)> _groups = [];
    private readonly List<(LayerNode Node, string Name)> _names = [];

    private DocumentSnapshot(LayerDocument document)
    {
        _width = document.Width;
        _height = document.Height;
        foreach (var group in document.EnumerateGroups())
            _groups.Add((group, group.Children.ToArray()));
        foreach (var node in document.EnumerateNodes())
            _names.Add((node, node.InternalName));
    }

    public static DocumentSnapshot Capture(LayerDocument document) => new(document);

    public void Restore(LayerDocument document)
    {
        document.Width = _width;
        document.Height = _height;
        foreach (var (group, children) in _groups)
            group.ReplaceChildren(children);
        foreach (var (node, name) in _names)
            node.InternalName = name;
        document.NormalizeRadios();
    }
}
