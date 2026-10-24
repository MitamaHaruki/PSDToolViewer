using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSDToolViewer.Core.Formats;

namespace PSDToolViewer.Core.Imaging;

/// <summary>PNG の読み書き（Windows Imaging Component を使用）。どのスレッドからでも呼べる。</summary>
public static class PngCodec
{
    public static PixelImage Decode(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new DocumentFormatException("画像を読み込めませんでした。");
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = frame.PixelWidth, h = frame.PixelHeight;
            var data = new byte[w * h * 4];
            frame.CopyPixels(data, w * 4, 0);
            return new PixelImage(w, h, data);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            throw new DocumentFormatException("画像を読み込めませんでした。PNG ファイルが壊れている可能性があります。", ex);
        }
    }

    public static PixelImage Decode(string path)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream);
    }

    public static void Encode(PixelImage image, Stream output)
    {
        var source = ToBitmapSource(image);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(output);
    }

    public static byte[] Encode(PixelImage image)
    {
        using var ms = new MemoryStream();
        Encode(image, ms);
        return ms.ToArray();
    }

    /// <summary>グレースケール（マスク用）を PNG にする。</summary>
    public static byte[] EncodeGray(int width, int height, byte[] data)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, data, width);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static byte[] DecodeGray(Stream stream, out int width, out int height)
    {
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Gray8)
            frame = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
        width = frame.PixelWidth;
        height = frame.PixelHeight;
        var data = new byte[width * height];
        frame.CopyPixels(data, width, 0);
        return data;
    }

    /// <summary>非乗算済みの画像を WPF の画像にする（凍結済み）。</summary>
    public static BitmapSource ToBitmapSource(PixelImage image)
    {
        var source = image.IsEmpty
            ? BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4)
            : BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Data, image.Stride);
        source.Freeze();
        return source;
    }
}
