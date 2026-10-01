using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Imaging.Render;
using MyDicomViewer.Core.Imaging;

namespace MyDicomViewer.Core.Ai;

/// <summary>
/// DICOM → モデル入力の前処理。学習側 (pneumonia-ai/preprocess.py) と完全に同じ手順:
///   1. RescaleSlope / RescaleIntercept を適用
///   2. MONOCHROME1 なら白黒反転
///   3. 画像全体の min-max で 0〜1 に正規化
///   4. 224x224 に面積平均で縮小 → 0〜255 に偶数丸め
///   5. /255 → 3ch に複製 → ImageNet の mean/std で標準化
/// </summary>
public static class PneumoniaPreprocessor
{
    public const int InputSize = 224;
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>手順 1〜4: DICOM → size x size の 8bit グレースケール</summary>
    public static byte[] ToGrayscale(DicomDataset dataset, int size = InputSize)
    {
        var (values, width, height, invert) = ReadPixels(dataset);
        float[] unit = ImageMath.NormalizeMinMax(values, invert);
        float[] resized = ImageMath.Resize(unit, width, height, size, size);
        return ImageMath.ToUInt8(resized);
    }

    /// <summary>手順 5: 8bit グレースケール → モデル入力 (3 x size x size, CHW)</summary>
    public static float[] ToInputTensor(byte[] gray)
    {
        int plane = gray.Length;
        var input = new float[3 * plane];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < plane; i++)
            {
                input[c * plane + i] = (gray[i] / 255f - Mean[c]) / Std[c];
            }
        }
        return input;
    }

    /// <summary>画素値（Rescale 適用済み）と反転の要否を読み出す。圧縮 DICOM は先に展開する。</summary>
    internal static (float[] Values, int Width, int Height, bool Invert) ReadPixels(DicomDataset dataset)
    {
        var ds = dataset.InternalTransferSyntax.IsEncapsulated
            ? new DicomTranscoder(dataset.InternalTransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian).Transcode(dataset)
            : dataset;

        if (!ds.Contains(DicomTag.PixelData))
            throw new InvalidDicomImageException("画像データ（Pixel Data）が含まれていません。");

        var dicomPixelData = DicomPixelData.Create(ds);
        if (dicomPixelData.SamplesPerPixel != 1)
            throw new InvalidDicomImageException("グレースケール画像のみ対応しています。");

        var pixels = PixelDataFactory.Create(dicomPixelData, 0);
        int w = pixels.Width, h = pixels.Height;

        float slope = (float)ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
        float intercept = (float)ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
        bool invert = ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "").Trim() == "MONOCHROME1";

        var values = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                values[y * w + x] = (float)pixels.GetPixel(x, y) * slope + intercept;
            }
        }
        return (values, w, h, invert);
    }
}

/// <summary>AI で扱えない DICOM のときの例外（メッセージはそのまま画面に出せる）</summary>
public sealed class InvalidDicomImageException(string message) : Exception(message);
