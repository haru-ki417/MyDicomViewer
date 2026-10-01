namespace MyDicomViewer.Core.Imaging;

/// <summary>BGRA（1画素4バイト）の画素データ。画面側でビットマップに変換して表示する。</summary>
public sealed record HeatmapPixels(byte[] Bgra, int Width, int Height);

/// <summary>Grad-CAM を元画像に重ねるための半透明カラーマップを作る</summary>
public static class HeatmapRenderer
{
    /// <summary>この値より弱い注目度は完全に透明にする（元画像を見やすくするため）</summary>
    public const double TransparentBelow = 0.15;

    /// <summary>
    /// cam (camSize x camSize) を元画像の縦横比に引き伸ばし、JET カラーマップの BGRA 画素にする。
    /// モデルは画像全体を正方形に縮小して見ているので、縦横を独立に拡大すれば元画像と位置が一致する。
    /// 出力は長辺 maxSide 以下に縮小する（表示時に元画像と同じ大きさに拡大されるため見た目は変わらない）。
    /// </summary>
    public static HeatmapPixels Render(float[] cam, int camSize, int imageWidth, int imageHeight, int maxSide = 1024)
    {
        if (cam.Length != camSize * camSize) throw new ArgumentException("CAM の要素数が camSize x camSize と一致しません。", nameof(cam));
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));

        double scale = Math.Min(1.0, (double)maxSide / Math.Max(imageWidth, imageHeight));
        int w = Math.Max(1, (int)Math.Round(imageWidth * scale));
        int h = Math.Max(1, (int)Math.Round(imageHeight * scale));

        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            double fy = (y + 0.5) * camSize / h - 0.5;
            for (int x = 0; x < w; x++)
            {
                double fx = (x + 0.5) * camSize / w - 0.5;
                double v = Math.Clamp(ImageMath.SampleBilinear(cam, camSize, camSize, fx, fy), 0, 1);

                int i = (y * w + x) * 4;
                bgra[i + 0] = ToByte(1.5 - Math.Abs(4 * v - 1)); // B
                bgra[i + 1] = ToByte(1.5 - Math.Abs(4 * v - 2)); // G
                bgra[i + 2] = ToByte(1.5 - Math.Abs(4 * v - 3)); // R
                bgra[i + 3] = ToByte((v - TransparentBelow) / (1 - TransparentBelow)); // A
            }
        }
        return new HeatmapPixels(bgra, w, h);
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
