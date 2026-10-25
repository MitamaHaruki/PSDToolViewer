using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSDToolViewer.Core.Formats;

namespace PSDToolViewer.Core.Imaging;

/// <summary>PNG の読み書き（Windows Imaging Component を使用）。どのスレッドからでも呼べる。</summary>
public static class PngCodec
{
    /// <param name="convertToSrgb">
    /// true のとき、埋め込まれた色プロファイル（Display P3 など）に従って sRGB に変換する（利用者の PNG 用）。
    /// false のときは画素の値をそのまま読む（.ptv 内の PNG 用）。
    /// </param>
    public static PixelImage Decode(Stream stream, bool convertToSrgb = false)
    {
        try
        {
            // 変換は Windows に任せず、PSD の表示と同じ計算（ColorTransform）で行う
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new DocumentFormatException("画像を読み込めませんでした。");
            var first = decoder.Frames[0];
            var profile = convertToSrgb ? ReadProfile(first) : null;
            BitmapSource frame = first;
            if (frame.Format != PixelFormats.Bgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = frame.PixelWidth, h = frame.PixelHeight;
            var data = new byte[w * h * 4];
            frame.CopyPixels(data, w * 4, 0);
            var image = new PixelImage(w, h, data);
            return profile is not null && ColorTransform.Create(profile, null) is { } transform ? transform.Apply(image) : image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            throw new DocumentFormatException("画像を読み込めませんでした。PNG ファイルが壊れている可能性があります。", ex);
        }
    }

    /// <summary>画像に埋め込まれた色プロファイル（ICC）を取り出す。なければ null。</summary>
    private static byte[]? ReadProfile(BitmapFrame frame)
    {
        try
        {
            if (frame.ColorContexts is not { Count: > 0 } contexts) return null;
            using var profile = contexts[0].OpenProfileStream();
            using var ms = new MemoryStream();
            profile.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static PixelImage Decode(string path, bool convertToSrgb = false)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream, convertToSrgb);
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
