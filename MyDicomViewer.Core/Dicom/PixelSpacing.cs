using FellowOakDicom;

namespace MyDicomViewer.Core.Dicom;

/// <summary>ピクセル間隔がどのタグから得られたか</summary>
public enum PixelSpacingSource
{
    /// <summary>(0028,0030) Pixel Spacing: 患者の体内での実寸（CT・MR など）</summary>
    PixelSpacing,

    /// <summary>(0018,1164) Imager Pixel Spacing: 検出器面での寸法（X線撮影。拡大率の補正前）</summary>
    ImagerPixelSpacing,
}

/// <summary>
/// 1画素あたりの実寸（mm）。DICOM の値は「行の間隔\列の間隔」の順なので、
/// 縦方向（行）の間隔が Row、横方向（列）の間隔が Column になる。
/// </summary>
public sealed record PixelSpacing(double Row, double Column, PixelSpacingSource Source)
{
    /// <summary>Pixel Spacing を優先し、無ければ Imager Pixel Spacing を使う。どちらも無ければ null。</summary>
    public static PixelSpacing? TryRead(DicomDataset dataset)
    {
        return TryReadTag(dataset, DicomTag.PixelSpacing, PixelSpacingSource.PixelSpacing)
            ?? TryReadTag(dataset, DicomTag.ImagerPixelSpacing, PixelSpacingSource.ImagerPixelSpacing);
    }

    /// <summary>画像上の2点（画素座標）間の距離 [mm]</summary>
    public double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = (x2 - x1) * Column;
        double dy = (y2 - y1) * Row;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static PixelSpacing? TryReadTag(DicomDataset dataset, DicomTag tag, PixelSpacingSource source)
    {
        try
        {
            if (dataset.TryGetValues<double>(tag, out var values) && values is { Length: >= 2 } && values[0] > 0 && values[1] > 0)
            {
                return new PixelSpacing(values[0], values[1], source);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or DicomDataException)
        {
            // 値が壊れている場合は「間隔なし」として扱う
        }
        return null;
    }
}
