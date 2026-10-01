namespace MyDicomViewer.Core.Imaging;

/// <summary>
/// 画像の正規化・リサイズなどの純粋な計算。
/// 学習側（pneumonia-ai/preprocess.py の numpy / OpenCV）と同じ結果になるように実装している。
/// </summary>
public static class ImageMath
{
    /// <summary>
    /// 画像全体の min-max で 0〜1 に正規化する。invert=true なら先に白黒反転する（MONOCHROME1 用）。
    /// 値が一定の画像は全て 0 になる。
    /// </summary>
    public static float[] NormalizeMinMax(ReadOnlySpan<float> source, bool invert)
    {
        var img = source.ToArray();
        if (img.Length == 0) return img;

        if (invert)
        {
            float max = Max(img);
            for (int i = 0; i < img.Length; i++) img[i] = max - img[i];
        }

        float lo = Min(img), hi = Max(img);
        float range = hi - lo;
        for (int i = 0; i < img.Length; i++)
        {
            img[i] = range > 0 ? (img[i] - lo) / range : 0f;
        }
        return img;
    }

    /// <summary>縮小は面積平均、拡大はバイリニアで src を dw x dh にする</summary>
    public static float[] Resize(float[] src, int sw, int sh, int dw, int dh) =>
        (sw >= dw && sh >= dh) ? ResizeArea(src, sw, sh, dw, dh) : ResizeBilinear(src, sw, sh, dw, dh);

    /// <summary>面積平均による縮小（OpenCV の INTER_AREA の縮小時と同じ考え方）</summary>
    public static float[] ResizeArea(float[] src, int sw, int sh, int dw, int dh)
    {
        Validate(src, sw, sh, dw, dh);
        var wx = AreaWeights(sw, dw);
        var wy = AreaWeights(sh, dh);

        var tmp = new float[dw * sh];
        for (int y = 0; y < sh; y++)
        {
            for (int dx = 0; dx < dw; dx++)
            {
                double acc = 0;
                foreach (var (sx, weight) in wx[dx]) acc += src[y * sw + sx] * weight;
                tmp[y * dw + dx] = (float)acc;
            }
        }

        var dst = new float[dw * dh];
        for (int dy = 0; dy < dh; dy++)
        {
            for (int dx = 0; dx < dw; dx++)
            {
                double acc = 0;
                foreach (var (sy, weight) in wy[dy]) acc += tmp[sy * dw + dx] * weight;
                dst[dy * dw + dx] = (float)acc;
            }
        }
        return dst;
    }

    /// <summary>バイリニア補間（ピクセル中心を合わせる方式。PyTorch の align_corners=False と同じ）</summary>
    public static float[] ResizeBilinear(float[] src, int sw, int sh, int dw, int dh)
    {
        Validate(src, sw, sh, dw, dh);
        var dst = new float[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                dst[y * dw + x] = SampleBilinear(src, sw, sh, (x + 0.5) * sw / dw - 0.5, (y + 0.5) * sh / dh - 0.5);
            }
        }
        return dst;
    }

    /// <summary>(fx, fy) の位置の値をバイリニア補間で取り出す（範囲外は端の値）</summary>
    public static float SampleBilinear(float[] src, int width, int height, double fx, double fy)
    {
        fx = Math.Clamp(fx, 0, width - 1);
        fy = Math.Clamp(fy, 0, height - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
        double tx = fx - x0, ty = fy - y0;
        double top = src[y0 * width + x0] * (1 - tx) + src[y0 * width + x1] * tx;
        double bottom = src[y1 * width + x0] * (1 - tx) + src[y1 * width + x1] * tx;
        return (float)(top * (1 - ty) + bottom * ty);
    }

    /// <summary>
    /// DICOM のウィンドウ処理（PS3.3 C.11.2.1.2 の線形関数）で 8bit に変換する。
    /// 中心 center・幅 width の範囲を 0〜255 に割り当て、範囲外は 0 または 255 にする。
    /// invert=true（MONOCHROME1）なら最後に白黒反転する。
    /// </summary>
    public static byte[] ApplyWindow(ReadOnlySpan<float> values, double center, double width, bool invert = false)
    {
        width = Math.Max(1, width);
        double lower = center - 0.5 - (width - 1) / 2;
        double upper = center - 0.5 + (width - 1) / 2;
        var result = new byte[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            double x = values[i];
            double y = x <= lower ? 0 : x > upper ? 255 : ((x - (center - 0.5)) / (width - 1) + 0.5) * 255;
            byte b = (byte)Math.Round(Math.Clamp(y, 0, 255));
            result[i] = invert ? (byte)(255 - b) : b;
        }
        return result;
    }

    /// <summary>0〜1 の値を 0〜255 に変換する。丸めは numpy と同じ偶数丸め。</summary>
    public static byte[] ToUInt8(ReadOnlySpan<float> unit)
    {
        var result = new byte[unit.Length];
        for (int i = 0; i < unit.Length; i++)
        {
            result[i] = (byte)Math.Round(Math.Clamp(unit[i] * 255.0, 0.0, 255.0), MidpointRounding.ToEven);
        }
        return result;
    }

    private static (int Index, double Weight)[][] AreaWeights(int src, int dst)
    {
        double scale = (double)src / dst;
        var table = new (int, double)[dst][];
        for (int d = 0; d < dst; d++)
        {
            double start = d * scale, end = (d + 1) * scale;
            int s0 = (int)Math.Floor(start), s1 = Math.Min((int)Math.Ceiling(end), src);
            var list = new List<(int, double)>(s1 - s0);
            for (int s = s0; s < s1; s++)
            {
                double overlap = Math.Min(end, s + 1) - Math.Max(start, s);
                if (overlap > 1e-9) list.Add((s, overlap / scale));
            }
            table[d] = list.ToArray();
        }
        return table;
    }

    private static void Validate(float[] src, int sw, int sh, int dw, int dh)
    {
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0) throw new ArgumentOutOfRangeException(nameof(sw), "画像サイズは正の値である必要があります。");
        if (src.Length != sw * sh) throw new ArgumentException($"画素数が一致しません（{src.Length} ≠ {sw}x{sh}）。", nameof(src));
    }

    private static float Min(float[] a) { float m = float.MaxValue; foreach (var v in a) if (v < m) m = v; return m; }
    private static float Max(float[] a) { float m = float.MinValue; foreach (var v in a) if (v > m) m = v; return m; }
}
