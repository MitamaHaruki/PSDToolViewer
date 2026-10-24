using PSDToolViewer.Core.Formats;
using PSDToolViewer.Core.Formats.Psd;
using PSDToolViewer.Core.Formats.Ptv;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;
using PSDToolViewer.Core.Rendering;

namespace PSDToolViewer.Core.Tests;

public class FormatRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PSDToolViewerTests", Guid.NewGuid().ToString("N"));

    public FormatRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static LayerDocument CreateSample()
    {
        var doc = new LayerDocument(64, 48);
        var face = new LayerGroup("!顔パーツ") { IsExpanded = true };
        var eyes = new LayerGroup("目") { IsExpanded = false, Opacity = 200, BlendMode = BlendMode.Normal };
        eyes.Add(new LayerItem("*通常", TestImages.Pattern(10, 8, 1), 5, 6));
        eyes.Add(new LayerItem("*閉じ", TestImages.Pattern(12, 4, 2), 7, 9) { IsVisible = false });
        face.Add(eyes);
        face.Add(new LayerItem("影", TestImages.Solid(20, 20, 10, 20, 30, 128), 0, 0)
        {
            BlendMode = BlendMode.Multiply,
            Clipping = true,
            Opacity = 100,
            FillOpacity = 50,
        });
        face.Add(new LayerItem("肌", TestImages.Solid(30, 30, 250, 220, 200), 2, 2)
        {
            Mask = new LayerMask(new PixelRect(4, 4, 10, 10), Enumerable.Range(0, 100).Select(i => (byte)(i * 2)).ToArray(), 0),
        });
        doc.Root.Add(face);
        doc.Root.Add(new LayerItem("体", TestImages.Pattern(40, 30, 3), 10, 15));
        doc.Root.Add(new LayerItem("空", PixelImage.Empty, 0, 0) { IsVisible = false });
        doc.ImageResources = [new ImageResource(0x03ED, string.Empty, [0, 72, 0, 0, 0, 1, 0, 1, 0, 72, 0, 0, 0, 1, 0, 1])];
        doc.NormalizeRadios();
        return doc;
    }

    private static void AssertSameTree(LayerGroup expected, LayerGroup actual)
    {
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (int i = 0; i < expected.Children.Count; i++)
        {
            var e = expected.Children[i];
            var a = actual.Children[i];
            Assert.Equal(e.InternalName, a.InternalName);
            Assert.Equal(e.IsShown, a.IsShown);
            Assert.Equal(e.Opacity, a.Opacity);
            Assert.Equal(e.FillOpacity, a.FillOpacity);
            Assert.Equal(e.BlendMode, a.BlendMode);
            Assert.Equal(e.Clipping, a.Clipping);
            Assert.Equal(e.Mask is null, a.Mask is null);
            if (e.Mask is { } em)
            {
                Assert.Equal(em.Bounds, a.Mask!.Bounds);
                Assert.Equal(em.DefaultColor, a.Mask.DefaultColor);
                Assert.Equal(em.Data, a.Mask.Data);
            }
            switch (e)
            {
                case LayerGroup eg:
                    var ag = Assert.IsType<LayerGroup>(a);
                    Assert.Equal(eg.IsExpanded, ag.IsExpanded);
                    AssertSameTree(eg, ag);
                    break;
                case LayerItem el:
                    var al = Assert.IsType<LayerItem>(a);
                    // 読み込み時に透明な余白が切り詰められることがあるため、不透明な範囲で比べる
                    Assert.Equal(el.OpaqueBounds, al.OpaqueBounds);
                    Assert.Equal(el.Image.Crop(el.Image.OpaqueBounds).Data, al.Image.Crop(al.Image.OpaqueBounds).Data);
                    break;
            }
        }
    }

    [Fact]
    public void PsdRoundTrip()
    {
        var doc = CreateSample();
        var path = Path.Combine(_dir, "sample.psd");
        DocumentIO.Save(doc, path);

        var loaded = PsdReader.Read(path);
        Assert.Equal(doc.Width, loaded.Width);
        Assert.Equal(doc.Height, loaded.Height);
        AssertSameTree(doc.Root, loaded.Root);
        Assert.Single(loaded.ImageResources);
        Assert.Equal(doc.ImageResources[0].Data, loaded.ImageResources[0].Data);
        Assert.False(loaded.HasUnsupportedFeatures);
    }

    [Fact]
    public void PtvRoundTrip()
    {
        var doc = CreateSample();
        var path = Path.Combine(_dir, "sample" + PtvFormat.Extension);
        DocumentIO.Save(doc, path);
        var loaded = DocumentIO.Load(path);
        Assert.Equal(doc.Width, loaded.Width);
        AssertSameTree(doc.Root, loaded.Root);
        Assert.Equal(doc.ImageResources[0].Data, loaded.ImageResources[0].Data);
    }

    [Fact]
    public void ExportWithTrimShiftsLayers()
    {
        var doc = new LayerDocument(100, 100);
        doc.Root.Add(new LayerItem("a", TestImages.Solid(10, 10, 255, 0, 0), 20, 30));
        doc.Root.Add(new LayerItem("b", TestImages.Solid(5, 5, 0, 255, 0), 50, 60) { IsVisible = false });
        var path = Path.Combine(_dir, "trim.psd");
        DocumentIO.ExportPsd(doc, path, trimTransparent: true);

        var loaded = PsdReader.Read(path);
        // 非表示のレイヤーも含めた範囲
        Assert.Equal(35, loaded.Width);
        Assert.Equal(35, loaded.Height);
        var a = Assert.IsType<LayerItem>(loaded.Root.Children[0]);
        Assert.Equal(new PixelRect(0, 0, 10, 10), a.Bounds);
        var b = Assert.IsType<LayerItem>(loaded.Root.Children[1]);
        Assert.Equal(new PixelRect(30, 30, 5, 5), b.Bounds);
    }

    [Fact]
    public void FolderLoadUsesNaturalOrderAndCanvasSize()
    {
        var root = Path.Combine(_dir, "folder");
        Directory.CreateDirectory(Path.Combine(root, "目"));
        WritePng(Path.Combine(root, "10.png"), TestImages.Solid(30, 20, 0, 0, 255));
        WritePng(Path.Combine(root, "2.png"), TestImages.Solid(10, 40, 255, 0, 0));
        WritePng(Path.Combine(root, "目", "閉じ.png"), TestImages.Solid(5, 5, 0, 255, 0));
        File.WriteAllText(Path.Combine(root, "memo.txt"), "無視される");

        var doc = DocumentIO.Load(root);
        Assert.Equal(30, doc.Width);
        Assert.Equal(40, doc.Height);
        Assert.Equal(new[] { "2", "10", "目" }, doc.Root.Children.Select(c => c.InternalName));
        var eyes = Assert.IsType<LayerGroup>(doc.Root.Children[2]);
        Assert.Equal("閉じ", eyes.Children[0].InternalName);
    }

    [Fact]
    public void PngImportTrimsTransparentMargin()
    {
        var data = new byte[20 * 10 * 4];
        int o = (4 * 20 + 6) * 4;
        data[o] = 1; data[o + 1] = 2; data[o + 2] = 3; data[o + 3] = 255;
        var path = Path.Combine(_dir, "dot.png");
        WritePng(path, new PixelImage(20, 10, data));

        var imported = ImageImport.LoadPng(path);
        Assert.Equal(20, imported.CanvasWidth);
        Assert.Equal(10, imported.CanvasHeight);
        Assert.Equal(new PixelRect(6, 4, 1, 1), imported.Layer.Bounds);
        Assert.Equal("dot", imported.Layer.InternalName);
    }

    [Fact]
    public void LoadRejectsUnsupportedPsd()
    {
        var path = Path.Combine(_dir, "bad.psd");
        File.WriteAllBytes(path, [.. "8BPS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 3, 0, 0, 0, 1, 0, 0, 0, 1, 0, 8, 0, 3]);
        var ex = Assert.Throws<DocumentFormatException>(() => DocumentIO.Load(path));
        Assert.Contains("PSB", ex.Message);
    }

    [Fact]
    public void CompositeIsWrittenAsMergedImage()
    {
        var doc = new LayerDocument(4, 4);
        doc.Root.Add(new LayerItem("赤", TestImages.Solid(4, 4, 255, 0, 0), 0, 0));
        var path = Path.Combine(_dir, "merged.psd");
        DocumentIO.Save(doc, path);
        var canvas = Compositor.RenderOnce(PsdReader.Read(path));
        Assert.Equal((byte)255, canvas.Data[2]);
        Assert.Equal((byte)255, canvas.Data[3]);
    }

    private static void WritePng(string path, PixelImage image)
    {
        using var stream = File.Create(path);
        PngCodec.Encode(image, stream);
    }
}
