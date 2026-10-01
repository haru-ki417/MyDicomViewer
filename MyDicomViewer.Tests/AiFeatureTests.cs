using FellowOakDicom;
using MyDicomViewer.Core.Ai;
using MyDicomViewer.Core.Imaging;

namespace MyDicomViewer.Tests;

public class WindowingTests
{
    [Fact]
    public void ApplyWindow_MapsRangeLinearly_AndClipsOutside()
    {
        byte[] result = ImageMath.ApplyWindow(new float[] { -1000, 75, 100, 125, 1000 }, center: 100, width: 50);

        Assert.Equal(0, result[0]);
        Assert.Equal(255, result[4]);
        Assert.Equal(130, result[2]); // ((100 - 99.5) / 49 + 0.5) * 255 ≈ 130.1
        Assert.True(result[1] < result[2] && result[2] < result[3]);
    }

    [Fact]
    public void ApplyWindow_Invert_FlipsOutput()
    {
        byte[] normal = ImageMath.ApplyWindow(new float[] { 0, 100, 200 }, 100, 200);
        byte[] inverted = ImageMath.ApplyWindow(new float[] { 0, 100, 200 }, 100, 200, invert: true);

        for (int i = 0; i < normal.Length; i++) Assert.Equal(255, normal[i] + inverted[i]);
    }
}

public class AiResultExporterTests
{
    private static PneumoniaResult Result(float probability, float[]? cam = null) =>
        new(probability, 0.54f, cam ?? new float[4 * 4], 4, TimeSpan.FromMilliseconds(90), "efficientnet_b0");

    [Fact]
    public void ComposeOverlay_WithoutAttention_KeepsImageGray()
    {
        var source = TestDicom.Gradient(20, 10);

        var (rgb, width, height) = AiResultExporter.ComposeOverlay(source, Result(0.1f), windowCenter: 400, windowWidth: 800);

        Assert.Equal((20, 10), (width, height));
        Assert.Equal(20 * 10 * 3, rgb.Length);
        for (int i = 0; i < rgb.Length; i += 3)
        {
            Assert.Equal(rgb[i], rgb[i + 1]);
            Assert.Equal(rgb[i], rgb[i + 2]);
        }
    }

    [Fact]
    public void ComposeOverlay_StrongAttention_TintsImage_AndDownscalesLargeImages()
    {
        var source = TestDicom.Gradient(300, 200);
        var hot = Enumerable.Repeat(1f, 4 * 4).ToArray();

        var (rgb, width, height) = AiResultExporter.ComposeOverlay(source, Result(0.9f, hot), 400, 800, maxSide: 100);

        Assert.Equal((100, 67), (width, height));
        Assert.Contains(Enumerable.Range(0, width * height), i => rgb[i * 3] != rgb[i * 3 + 2]); // 赤みが付く
    }

    [Fact]
    public void CreateSecondaryCapture_IsNewSeriesInSameStudy_WithTraceability()
    {
        var source = TestDicom.Gradient(8, 6);
        var rgb = new byte[8 * 6 * 3];

        var ds = AiResultExporter.CreateSecondaryCapture(source, rgb, 8, 6, Result(0.7f), "efficientnet_b0", "1.2.0");

        Assert.Equal(DicomUID.SecondaryCaptureImageStorage, ds.GetSingleValue<DicomUID>(DicomTag.SOPClassUID));
        Assert.Equal(source.GetString(DicomTag.StudyInstanceUID), ds.GetString(DicomTag.StudyInstanceUID));
        Assert.NotEqual(source.GetString(DicomTag.SeriesInstanceUID), ds.GetString(DicomTag.SeriesInstanceUID));
        Assert.Equal(source.GetString(DicomTag.PatientName), ds.GetString(DicomTag.PatientName));
        Assert.Equal((ushort)3, ds.GetSingleValue<ushort>(DicomTag.SamplesPerPixel));
        Assert.Equal((ushort)6, ds.GetSingleValue<ushort>(DicomTag.Rows));
        Assert.Equal((ushort)8, ds.GetSingleValue<ushort>(DicomTag.Columns));

        string comment = ds.GetString(DicomTag.ImageComments);
        Assert.Contains("0.700", comment);
        Assert.Contains("POSITIVE", comment);
        Assert.Contains("not for diagnosis", comment);

        var reference = Assert.Single(ds.GetSequence(DicomTag.SourceImageSequence).Items);
        Assert.Equal(source.GetString(DicomTag.SOPInstanceUID), reference.GetString(DicomTag.ReferencedSOPInstanceUID));
    }

    [Fact]
    public void CreateSecondaryCapture_CanBeSavedAndReopened()
    {
        var source = TestDicom.Gradient(8, 6);
        var ds = AiResultExporter.CreateSecondaryCapture(source, new byte[8 * 6 * 3], 8, 6, Result(0.2f), "m", "1.0.0");

        using var stream = new MemoryStream();
        new DicomFile(ds).Save(stream);
        stream.Position = 0;
        var reopened = DicomFile.Open(stream);

        Assert.Equal("NEGATIVE", reopened.Dataset.GetString(DicomTag.ImageComments).Split("result ")[1].Split('.')[0]);
        Assert.True(reopened.Dataset.Contains(DicomTag.PixelData));
    }
}
