using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Tests;

public class DocumentOperationsTests
{
    private static LayerItem Layer(string name, bool visible = true)
        => new(name, TestImages.Solid(2, 2, 255, 0, 0), 0, 0) { IsVisible = visible };

    private static (LayerDocument Doc, LayerGroup Eyes, LayerItem A, LayerItem B, LayerItem C, LayerItem Body) Create()
    {
        var doc = new LayerDocument(10, 10);
        var eyes = new LayerGroup("目");
        var a = Layer("*通常");
        var b = Layer("*閉じ");
        var c = Layer("*笑い");
        eyes.Add(a);
        eyes.Add(b);
        eyes.Add(c);
        var body = Layer("!体");
        doc.Root.Add(eyes);
        doc.Root.Add(body);
        doc.NormalizeRadios();
        return (doc, eyes, a, b, c, body);
    }

    [Fact]
    public void RadioKeepsOnlyTopVisibleAfterNormalize()
    {
        var (_, _, a, b, c, _) = Create();
        Assert.True(a.IsVisible);
        Assert.False(b.IsVisible);
        Assert.False(c.IsVisible);
    }

    [Fact]
    public void SelectingRadioHidesSiblings()
    {
        var (_, _, a, b, c, _) = Create();
        Assert.True(DocumentOperations.SetVisible(c, true));
        Assert.False(a.IsVisible);
        Assert.False(b.IsVisible);
        Assert.True(c.IsVisible);
        // ラジオボタンは外せない
        Assert.False(DocumentOperations.SetVisible(c, false));
        Assert.True(c.IsVisible);
    }

    [Fact]
    public void ForcedIgnoresVisibility()
    {
        var (_, _, _, _, _, body) = Create();
        Assert.False(DocumentOperations.SetVisible(body, false));
        body.IsVisible = false;
        Assert.True(body.IsShown);
    }

    [Fact]
    public void ToggleRadioAddsThenRemoves()
    {
        var doc = new LayerDocument(10, 10);
        var x = Layer("x");
        var y = Layer("!y");
        doc.Root.Add(x);
        doc.Root.Add(y);

        DocumentOperations.ToggleRadio(doc, [x, y]);
        Assert.Equal("*x", x.InternalName);
        Assert.Equal("*y", y.InternalName);
        Assert.Equal(1, doc.Root.Children.Count(n => n.IsVisible));

        DocumentOperations.ToggleRadio(doc, [x, y]);
        Assert.Equal("x", x.InternalName);
        Assert.Equal("y", y.InternalName);
    }

    [Fact]
    public void ToggleForcedReplacesRadioMark()
    {
        var (doc, _, a, b, _, _) = Create();
        DocumentOperations.ToggleForced(doc, [b]);
        Assert.Equal("!閉じ", b.InternalName);
        Assert.True(b.IsShown);
        Assert.True(a.IsVisible);
        DocumentOperations.ToggleForced(doc, [b]);
        Assert.Equal("閉じ", b.InternalName);
        Assert.True(b.IsVisible);
    }

    [Fact]
    public void RenameKeepsMarks()
    {
        var (_, _, a, _, _, body) = Create();
        Assert.True(DocumentOperations.Rename(a, "新しい目"));
        Assert.Equal("*新しい目", a.InternalName);
        Assert.Equal("新しい目", a.DisplayName);
        // 入力した記号は取り除く
        Assert.True(DocumentOperations.Rename(body, "*からだ"));
        Assert.Equal("!からだ", body.InternalName);
        Assert.False(DocumentOperations.Rename(body, "  "));
    }

    [Fact]
    public void CreateGroupAboveTopmostSelection()
    {
        var (doc, eyes, a, b, _, _) = Create();
        var group = DocumentOperations.CreateGroup(doc, [b, a]);
        Assert.Same(eyes, group.Parent);
        Assert.Equal(0, eyes.IndexOf(group));
        Assert.Equal(1, eyes.IndexOf(a));

        var bottom = DocumentOperations.CreateGroup(doc, []);
        Assert.Same(doc.Root, bottom.Parent);
        Assert.Equal(doc.Root.Children.Count - 1, doc.Root.IndexOf(bottom));
    }

    [Fact]
    public void MoveIntoGroupAndReorder()
    {
        var (doc, eyes, a, _, c, body) = Create();
        Assert.True(DocumentOperations.Move(doc, [body], eyes, 1));
        Assert.Same(eyes, body.Parent);
        Assert.Equal(new LayerNode[] { a, body }, eyes.Children.Take(2));

        // 自分の中には移動できない
        Assert.False(DocumentOperations.Move(doc, [eyes], eyes, 0));

        // 同じ位置への移動は変更なし
        Assert.False(DocumentOperations.Move(doc, [c], eyes, eyes.Children.Count));
    }

    [Fact]
    public void MoveSkipsDescendantsOfSelectedGroups()
    {
        var (doc, eyes, a, _, _, body) = Create();
        var target = new LayerGroup("移動先");
        doc.Root.Add(target);
        Assert.True(DocumentOperations.Move(doc, [eyes, a], target, 0));
        Assert.Same(target, eyes.Parent);
        Assert.Same(eyes, a.Parent);
        Assert.Same(doc.Root, body.Parent);
    }

    [Fact]
    public void UndoRedoRestoresStructureAndNames()
    {
        var (doc, eyes, a, b, _, body) = Create();
        var history = new EditHistory();
        long initial = history.StateId;

        history.Apply(doc, () => DocumentOperations.Rename(a, "変更"));
        history.Apply(doc, () => { DocumentOperations.Delete(doc, [eyes]); return true; });
        Assert.Single(doc.Root.Children);

        Assert.True(history.Undo(doc));
        Assert.Same(eyes, doc.Root.Children[0]);
        Assert.Equal("*変更", a.InternalName);

        Assert.True(history.Undo(doc));
        Assert.Equal("*通常", a.InternalName);
        Assert.Equal(initial, history.StateId);

        Assert.True(history.Redo(doc));
        Assert.Equal("*変更", a.InternalName);
        Assert.NotEqual(initial, history.StateId);

        // 変更がなければ履歴に積まない
        Assert.False(history.Apply(doc, () => DocumentOperations.Rename(b, "閉じ")));
        Assert.Same(doc.Root, body.Parent);
    }

    [Fact]
    public void AddLayersExpandsCanvas()
    {
        var doc = new LayerDocument(10, 10);
        var big = new LayerItem("大", TestImages.Solid(20, 5, 0, 0, 0), 3, 0);
        DocumentOperations.AddLayers(doc, [big]);
        Assert.Equal(23, doc.Width);
        Assert.Equal(10, doc.Height);
        Assert.Same(big, doc.Root.Children[^1]);
    }

    [Fact]
    public void EffectiveVisibilityFollowsParents()
    {
        var (_, eyes, a, _, _, _) = Create();
        Assert.True(a.IsEffectivelyVisible);
        eyes.IsVisible = false;
        Assert.False(a.IsEffectivelyVisible);
        Assert.Equal(1, a.Depth);
        Assert.Equal(PixelRect.Empty, PixelImage.Empty.OpaqueBounds);
    }
}
