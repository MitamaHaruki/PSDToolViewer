using System.Windows.Media;
using System.Windows.Media.Imaging;
using PSDToolViewer.Core.Rendering;

namespace PSDToolViewer.Core.Imaging;

/// <summary>
/// 色プロファイル間の変換。プロファイルの指定が null のときは sRGB とみなす。
/// 行列とトーンカーブで表される一般的な RGB プロファイルは画素ごとに正確に計算し、
/// それ以外は Windows のカラー管理（WIC）で作った 3D LUT を補間して使う。
/// </summary>
public sealed class ColorTransform
{
    private const int GridSize = 65;
    private const int OutputSize = 16384;

    // 行列型：入力の各値を線形に戻す表 → 3×3 行列 → 出力の表（√ で引いて暗部の精度を保つ）
    private readonly float[]? _inR, _inG, _inB;
    private readonly float[] _m = new float[9];
    private readonly byte[]? _outR, _outG, _outB;

    // LUT 型：[(b * N + g) * N + r] * 3 = (R, G, B)
    private readonly byte[]? _lut;
    private readonly int[] _index = new int[256];
    private readonly float[] _weight = new float[256];

    private ColorTransform(IccProfile source, IccProfile destination)
    {
        var m = IccProfile.Multiply(IccProfile.Invert(destination.ToXyz), source.ToXyz);
        for (int i = 0; i < 9; i++) _m[i] = (float)m[i / 3, i % 3];
        _inR = InputTable(source.Curves[0]);
        _inG = InputTable(source.Curves[1]);
        _inB = InputTable(source.Curves[2]);
        _outR = OutputTable(destination.Curves[0]);
        _outG = OutputTable(destination.Curves[1]);
        _outB = OutputTable(destination.Curves[2]);
    }

    private ColorTransform(byte[] lut)
    {
        _lut = lut;
        for (int v = 0; v < 256; v++)
        {
            float f = v * (GridSize - 1) / 255f;
            int i = Math.Min(GridSize - 2, (int)f);
            _index[v] = i;
            _weight[v] = f - i;
        }
    }

    private static float[] InputTable(Func<double, double> curve)
    {
        var table = new float[256];
        for (int v = 0; v < 256; v++) table[v] = (float)Math.Clamp(curve(v / 255.0), 0, 1);
        return table;
    }

    /// <summary>線形の値 y（添字は √y）から、トーンカーブの逆関数で 0～255 の値を求める表。</summary>
    private static byte[] OutputTable(Func<double, double> curve)
    {
        var table = new byte[OutputSize];
        for (int i = 0; i < OutputSize; i++)
        {
            double s = (double)i / (OutputSize - 1);
            double y = s * s;
            double lo = 0, hi = 1;
            for (int k = 0; k < 40; k++)
            {
                double mid = (lo + hi) / 2;
                if (curve(mid) < y) lo = mid;
                else hi = mid;
            }
            table[i] = (byte)Math.Clamp(Math.Round((lo + hi) / 2 * 255), 0, 255);
        }
        return table;
    }

    /// <summary>
    /// 変換を作る。変換が不要（実質 sRGB 同士など）な場合や、プロファイルが読めない場合は null。
    /// </summary>
    public static ColorTransform? Create(byte[]? sourceProfile, byte[]? destinationProfile)
    {
        if (sourceProfile is null && destinationProfile is null) return null;
        if (sourceProfile is not null && destinationProfile is not null && sourceProfile.AsSpan().SequenceEqual(destinationProfile)) return null;

        var source = sourceProfile is null ? IccProfile.Srgb : IccProfile.TryParse(sourceProfile, out var s) ? s : null;
        var destination = destinationProfile is null ? IccProfile.Srgb : IccProfile.TryParse(destinationProfile, out var d) ? d : null;
        ColorTransform? transform;
        if (source is not null && destination is not null)
        {
            try
            {
                transform = new ColorTransform(source, destination);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        else
        {
            transform = CreateWithWindows(sourceProfile, destinationProfile);
        }
        return transform is null || transform.IsIdentity() ? null : transform;
    }

    /// <summary>ほぼ値が変わらない（sRGB 相当のプロファイル）か。</summary>
    private bool IsIdentity()
    {
        for (int r = 0; r < 256; r += 15)
            for (int g = 0; g < 256; g += 15)
                for (int b = 0; b < 256; b += 15)
                {
                    Map(r, g, b, out int or, out int og, out int ob);
                    if (Math.Abs(or - r) > 1 || Math.Abs(og - g) > 1 || Math.Abs(ob - b) > 1) return false;
                }
        return true;
    }

    private static ColorTransform? CreateWithWindows(byte[]? sourceProfile, byte[]? destinationProfile)
    {
        var tempFiles = new List<string>();
        try
        {
            var source = CreateContext(sourceProfile, tempFiles);
            var destination = CreateContext(destinationProfile, tempFiles);
            if (source is null || destination is null) return null;

            // 格子点の色を並べた画像を作り、まとめて変換する
            int width = GridSize * GridSize, height = GridSize;
            var grid = new byte[width * height * 4];
            for (int b = 0; b < GridSize; b++)
                for (int g = 0; g < GridSize; g++)
                    for (int r = 0; r < GridSize; r++)
                    {
                        int o = (b * width + g * GridSize + r) * 4;
                        grid[o] = Level(b);
                        grid[o + 1] = Level(g);
                        grid[o + 2] = Level(r);
                        grid[o + 3] = 255;
                    }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, grid, width * 4);
            var converted = new ColorConvertedBitmap(bitmap, source, destination, PixelFormats.Bgra32);
            var result = new byte[width * height * 4];
            converted.CopyPixels(result, width * 4, 0);

            var lut = new byte[GridSize * GridSize * GridSize * 3];
            for (int i = 0; i < GridSize * GridSize * GridSize; i++)
            {
                lut[i * 3] = result[i * 4 + 2];
                lut[i * 3 + 1] = result[i * 4 + 1];
                lut[i * 3 + 2] = result[i * 4];
            }
            return new ColorTransform(lut);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException or InvalidOperationException or FileFormatException)
        {
            return null;
        }
        finally
        {
            foreach (var file in tempFiles)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static byte Level(int i) => (byte)Math.Round(i * 255.0 / (GridSize - 1));

    private static ColorContext? CreateContext(byte[]? profile, List<string> tempFiles)
    {
        if (profile is null) return new ColorContext(PixelFormats.Bgra32);
        if (profile.Length < 128) return null;
        // ColorContext はファイルからしか作れないため、一時ファイルに書き出す
        var path = Path.Combine(Path.GetTempPath(), $"psdtv-{Guid.NewGuid():N}.icc");
        File.WriteAllBytes(path, profile);
        tempFiles.Add(path);
        return new ColorContext(new Uri(path));
    }

    /// <summary>1 画素分（非乗算済み）を変換する。</summary>
    private void Map(int r, int g, int b, out int or, out int og, out int ob)
    {
        if (_lut is null)
        {
            float lr = _inR![r], lg = _inG![g], lb = _inB![b];
            var m = _m;
            or = _outR![OutputIndex(m[0] * lr + m[1] * lg + m[2] * lb)];
            og = _outG![OutputIndex(m[3] * lr + m[4] * lg + m[5] * lb)];
            ob = _outB![OutputIndex(m[6] * lr + m[7] * lg + m[8] * lb)];
            return;
        }

        int ri = _index[r], gi = _index[g], bi = _index[b];
        float rw = _weight[r], gw = _weight[g], bw = _weight[b];
        const int N = GridSize;
        int c000 = ((bi * N + gi) * N + ri) * 3;
        int c100 = c000 + 3;
        int c010 = c000 + N * 3;
        int c110 = c010 + 3;
        int c001 = c000 + N * N * 3;
        int c101 = c001 + 3;
        int c011 = c001 + N * 3;
        int c111 = c011 + 3;
        var l = _lut;
        float Channel(int k)
        {
            float x00 = l[c000 + k] + (l[c100 + k] - l[c000 + k]) * rw;
            float x10 = l[c010 + k] + (l[c110 + k] - l[c010 + k]) * rw;
            float x01 = l[c001 + k] + (l[c101 + k] - l[c001 + k]) * rw;
            float x11 = l[c011 + k] + (l[c111 + k] - l[c011 + k]) * rw;
            float y0 = x00 + (x10 - x00) * gw;
            float y1 = x01 + (x11 - x01) * gw;
            return y0 + (y1 - y0) * bw;
        }
        or = (int)(Channel(0) + 0.5f);
        og = (int)(Channel(1) + 0.5f);
        ob = (int)(Channel(2) + 0.5f);
    }

    private static int OutputIndex(float linear)
    {
        // 色域外は切り詰める
        if (linear <= 0) return 0;
        if (linear >= 1) return OutputSize - 1;
        return (int)(MathF.Sqrt(linear) * (OutputSize - 1) + 0.5f);
    }

    /// <summary>非乗算済みの画像を変換した新しい画像を返す。</summary>
    public PixelImage Apply(PixelImage image)
    {
        if (image.IsEmpty) return image;
        var src = image.Data;
        var dst = new byte[src.Length];
        int width = image.Width;
        Parallel.For(0, image.Height, y =>
        {
            int o = y * width * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                Map(src[o + 2], src[o + 1], src[o], out int r, out int g, out int b);
                dst[o] = (byte)b;
                dst[o + 1] = (byte)g;
                dst[o + 2] = (byte)r;
                dst[o + 3] = src[o + 3];
            }
        });
        return new PixelImage(image.Width, image.Height, dst);
    }

    /// <summary>乗算済み (Pbgra32) のデータを変換して <paramref name="destination"/> に書く（同じ配列でもよい）。</summary>
    public void ApplyPremultiplied(byte[] source, byte[] destination, int width, int height)
    {
        Parallel.For(0, height, y =>
        {
            int o = y * width * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                int a = source[o + 3];
                if (a == 0)
                {
                    destination[o] = destination[o + 1] = destination[o + 2] = destination[o + 3] = 0;
                    continue;
                }
                int sb = source[o], sg = source[o + 1], sr = source[o + 2];
                if (a != 255)
                {
                    sb = Math.Min(255, (sb * 255 + a / 2) / a);
                    sg = Math.Min(255, (sg * 255 + a / 2) / a);
                    sr = Math.Min(255, (sr * 255 + a / 2) / a);
                }
                Map(sr, sg, sb, out int r, out int g, out int b);
                if (a != 255)
                {
                    r = (r * a + 127) / 255;
                    g = (g * a + 127) / 255;
                    b = (b * a + 127) / 255;
                }
                destination[o] = (byte)b;
                destination[o + 1] = (byte)g;
                destination[o + 2] = (byte)r;
                destination[o + 3] = (byte)a;
            }
        });
    }

    /// <summary>作業領域の内容を変換した乗算済みデータを返す。</summary>
    public byte[] ApplyPremultiplied(Canvas canvas)
    {
        var result = new byte[canvas.Data.Length];
        ApplyPremultiplied(canvas.Data, result, canvas.Width, canvas.Height);
        return result;
    }
}
