using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;
using PSDToolViewer.Core.Rendering;

namespace PSDToolViewer.Core.Tests;

public class CompositorTests
{
    private static (byte B, byte G, byte R, byte A) At(Canvas canvas, int x, int y)
    {
        int i = (y * canvas.Width + x) * 4;
        return (canvas.Data[i], canvas.Data[i + 1], canvas.Data[i + 2], canvas.Data[i + 3]);
    }

    [Fact]
    public void NormalOverAndHidden()
    {
        var doc = new LayerDocument(4, 4);
        doc.Root.Add(new LayerItem("非表示", TestImages.Solid(4, 4, 0, 0, 255), 0, 0) { IsVisible = false });
        doc.Root.Add(new LayerItem("赤半透明", TestImages.Solid(2, 2, 255, 0, 0, 128), 0, 0));
        doc.Root.Add(new LayerItem("緑", TestImages.Solid(4, 4, 0, 255, 0), 0, 0));

        var canvas = Compositor.RenderOnce(doc);
        var p = At(canvas, 0, 0);
        Assert.Equal(255, p.A);
        Assert.InRange(p.R, 126, 130);
        Assert.InRange(p.G, 125, 129);
        Assert.Equal(0, p.B);
        Assert.Equal((0, 255, 0, 255), At(canvas, 3, 3));
    }

    [Fact]
    public void ForcedLayerIsDrawnEvenIfHidden()
    {
        var doc = new LayerDocument(2, 2);
        doc.Root.Add(new LayerItem("!常に", TestImages.Solid(2, 2, 255, 0, 0), 0, 0) { IsVisible = false });
        Assert.Equal((0, 0, 255, 255), At(Compositor.RenderOnce(doc), 1, 1));
    }

    [Fact]
    public void ClippingIsLimitedToBaseAlpha()
    {
        var doc = new LayerDocument(4, 1);
        // 上から：クリッピングされた青（全面）、土台の赤（左 2px）
        doc.Root.Add(new LayerItem("青", TestImages.Solid(4, 1, 0, 0, 255), 0, 0) { Clipping = true });
        doc.Root.Add(new LayerItem("赤", TestImages.Solid(2, 1, 255, 0, 0), 0, 0));

        var canvas = Compositor.RenderOnce(doc);
        Assert.Equal((255, 0, 0, 255), At(canvas, 0, 0));
        Assert.Equal((0, 0, 0, 0), At(canvas, 3, 0));
    }

    [Fact]
    public void HiddenBaseHidesClippedLayers()
    {
        var doc = new LayerDocument(2, 1);
        doc.Root.Add(new LayerItem("青", TestImages.Solid(2, 1, 0, 0, 255), 0, 0) { Clipping = true });
        doc.Root.Add(new LayerItem("赤", TestImages.Solid(2, 1, 255, 0, 0), 0, 0) { IsVisible = false });
        Assert.Equal((0, 0, 0, 0), At(Compositor.RenderOnce(doc), 0, 0));
    }

    [Fact]
    public void MultiplyBlend()
    {
        var doc = new LayerDocument(1, 1);
        doc.Root.Add(new LayerItem("乗算", TestImages.Solid(1, 1, 128, 128, 128), 0, 0) { BlendMode = BlendMode.Multiply });
        doc.Root.Add(new LayerItem("下地", TestImages.Solid(1, 1, 200, 100, 50), 0, 0));
        var p = At(Compositor.RenderOnce(doc), 0, 0);
        Assert.InRange(p.R, 99, 101);
        Assert.InRange(p.G, 49, 51);
        Assert.InRange(p.B, 24, 26);
    }

    [Fact]
    public void GroupOpacityAndMask()
    {
        var doc = new LayerDocument(2, 1);
        var group = new LayerGroup("フォルダ") { Opacity = 128 };
        group.Add(new LayerItem("白", TestImages.Solid(2, 1, 255, 255, 255), 0, 0));
        doc.Root.Add(group);
        var canvas = Compositor.RenderOnce(doc);
        Assert.InRange(At(canvas, 0, 0).A, 127, 129);

        group.Opacity = 255;
        group.Mask = new LayerMask(new PixelRect(0, 0, 1, 1), [255], 0);
        canvas = Compositor.RenderOnce(doc);
        Assert.Equal(255, At(canvas, 0, 0).A);
        Assert.Equal(0, At(canvas, 1, 0).A);
    }

    [Fact]
    public void HiddenGroupHidesChildren()
    {
        var doc = new LayerDocument(1, 1);
        var group = new LayerGroup("フォルダ") { IsVisible = false };
        group.Add(new LayerItem("!強制", TestImages.Solid(1, 1, 255, 255, 255), 0, 0));
        doc.Root.Add(group);
        Assert.Equal(0, At(Compositor.RenderOnce(doc), 0, 0).A);
    }

    [Fact]
    public void CanvasConversionAndBounds()
    {
        var doc = new LayerDocument(10, 10);
        doc.Root.Add(new LayerItem("点", TestImages.Solid(2, 3, 10, 20, 30, 128), 4, 5));
        var canvas = Compositor.RenderOnce(doc);
        Assert.Equal(new PixelRect(4, 5, 2, 3), canvas.ComputeOpaqueBounds());
        var image = canvas.ToStraightImage(new PixelRect(4, 5, 2, 3));
        var p = TestImages.Pixel(image, 0, 0);
        Assert.Equal(128, p.A);
        Assert.InRange(p.R, 9, 11);
        Assert.InRange(p.G, 19, 21);
        Assert.InRange(p.B, 29, 31);
    }
}
