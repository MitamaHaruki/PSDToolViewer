using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSDToolViewer.Core.Formats;
using PSDToolViewer.Core.Imaging;
using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Tests;

public class ColorTransformTests
{
    private const string ColorFolder = @"C:\Windows\System32\spool\drivers\color";
    private static readonly string AdobeRgbPath = Path.Combine(ColorFolder, "AdobeRGB1998.icc");
    private static readonly string SrgbPath = Path.Combine(ColorFolder, "sRGB Color Space Profile.icm");

    [Fact]
    public void SrgbProfileNeedsNoTransform()
    {
        Assert.Null(ColorTransform.Create(null, null));
        if (!File.Exists(SrgbPath)) return;
        Assert.Null(ColorTransform.Create(File.ReadAllBytes(SrgbPath), null));
    }

    [Fact]
    public void BrokenProfileIsIgnored()
    {
        Assert.Null(ColorTransform.Create(new byte[200], null));
        Assert.Null(ColorTransform.Create([1, 2, 3], null));
    }

    [Fact]
    public void AdobeRgbToSrgbMatchesStandardFormula()
    {
        // Adobe RGB のプロファイルがない環境では確認できない
        if (!File.Exists(AdobeRgbPath)) return;
        var transform = ColorTransform.Create(File.ReadAllBytes(AdobeRgbPath), null);
        Assert.NotNull(transform);

        // Adobe RGB (100,150,200) → 線形化 → XYZ → sRGB の計算値は (66,151,203)
        var p = TestImages.Pixel(transform.Apply(TestImages.Solid(1, 1, 100, 150, 200)), 0, 0);
        Assert.InRange(p.R, 65, 67);
        Assert.InRange(p.G, 150, 152);
        Assert.InRange(p.B, 202, 204);

        // 白・黒はそのまま、灰色は灰色のまま
        Assert.Equal((255, 255, 255, 255), TestImages.Pixel(transform.Apply(TestImages.Solid(1, 1, 255, 255, 255)), 0, 0));
        Assert.Equal((0, 0, 0, 255), TestImages.Pixel(transform.Apply(TestImages.Solid(1, 1, 0, 0, 0)), 0, 0));
        var gray = TestImages.Pixel(transform.Apply(TestImages.Solid(1, 1, 128, 128, 128)), 0, 0);
        Assert.InRange(Math.Abs(gray.R - gray.G) + Math.Abs(gray.G - gray.B), 0, 2);
    }

    [Fact]
    public void SrgbRoundTripThroughWideGamut()
    {
        if (!File.Exists(AdobeRgbPath)) return;
        var icc = File.ReadAllBytes(AdobeRgbPath);
        var random = new Random(3);
        var data = new byte[4096 * 4];
        random.NextBytes(data);
        for (int i = 3; i < data.Length; i += 4) data[i] = 255;
        var back = ColorTransform.Create(icc, null)!.Apply(ColorTransform.Create(null, icc)!.Apply(new PixelImage(4096, 1, data)));
        long sum = 0;
        for (int i = 0; i < data.Length; i++)
            if (i % 4 != 3) sum += Math.Abs(back.Data[i] - data[i]);
        // 8bit の量子化による差だけが残る
        Assert.True(sum / (4096.0 * 3) < 0.5);
    }

    [Fact]
    public void NonRgbProfileIsIgnoredWithoutError()
    {
        var cmyk = Path.Combine(ColorFolder, "JapanColor2001Coated.icc");
        if (!File.Exists(cmyk)) return;
        Assert.Null(ColorTransform.Create(File.ReadAllBytes(cmyk), null));
    }

    [Fact]
    public void PremultipliedKeepsAlphaAndTransparentPixels()
    {
        if (!File.Exists(AdobeRgbPath)) return;
        var transform = ColorTransform.Create(File.ReadAllBytes(AdobeRgbPath), null)!;
        byte[] src = [0, 0, 0, 0, 50, 60, 70, 128];
        var dst = new byte[src.Length];
        transform.ApplyPremultiplied(src, dst, 2, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, dst[..4]);
        Assert.Equal(128, dst[7]);
        Assert.All(dst[4..7], v => Assert.True(v <= 128));
    }

    [Fact]
    public void PngWithEmbeddedProfileIsConvertedOnImport()
    {
        if (!File.Exists(AdobeRgbPath)) return;
        byte[] pixel = [200, 150, 100, 255];
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixel, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source, null, null, new ReadOnlyCollection<ColorContext>([new ColorContext(new Uri(AdobeRgbPath))])));
        var path = Path.Combine(Path.GetTempPath(), $"psdtv-test-{Guid.NewGuid():N}.png");
        try
        {
            using (var stream = File.Create(path)) encoder.Save(stream);
            var layer = ImageImport.LoadPng(path).Layer;
            var p = TestImages.Pixel(layer.Image, 0, 0);
            // Adobe RGB の (100,150,200) は sRGB では赤が小さくなる
            Assert.InRange(p.R, 60, 70);
            Assert.InRange(p.G, 148, 154);
            Assert.InRange(p.B, 200, 206);

            // .ptv 内の PNG は値をそのまま読む
            using var raw = File.OpenRead(path);
            Assert.Equal((byte)100, TestImages.Pixel(PngCodec.Decode(raw), 0, 0).R);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DocumentExposesIccProfileResource()
    {
        var doc = new LayerDocument(1, 1);
        Assert.Null(doc.ColorProfile);
        doc.ImageResources = [new ImageResource(LayerDocument.IccProfileResourceId, string.Empty, [1, 2, 3])];
        Assert.Equal(new byte[] { 1, 2, 3 }, doc.ColorProfile);
    }
}
