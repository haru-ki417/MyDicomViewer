using FellowOakDicom;
using MyDicomViewer.Core.Dicom;
using MyDicomViewer.Core.Imaging;

namespace MyDicomViewer.Tests;

public class PixelSpacingTests
{
    [Fact]
    public void TryRead_PrefersPixelSpacing_OverImagerPixelSpacing()
    {
        var ds = TestDicom.Gradient(4, 4);
        ds.AddOrUpdate(DicomTag.ImagerPixelSpacing, 0.2m, 0.2m);
        ds.AddOrUpdate(DicomTag.PixelSpacing, 0.5m, 0.25m);

        var spacing = PixelSpacing.TryRead(ds);

        Assert.NotNull(spacing);
        Assert.Equal(0.5, spacing!.Row, 6);
        Assert.Equal(0.25, spacing.Column, 6);
        Assert.Equal(PixelSpacingSource.PixelSpacing, spacing.Source);
    }

    [Fact]
    public void TryRead_FallsBackToImagerPixelSpacing_ForRadiographs()
    {
        var ds = TestDicom.Gradient(4, 4);
        ds.AddOrUpdate(DicomTag.ImagerPixelSpacing, 0.143m, 0.143m);

        var spacing = PixelSpacing.TryRead(ds);

        Assert.Equal(PixelSpacingSource.ImagerPixelSpacing, spacing!.Source);
    }

    [Fact]
    public void TryRead_ReturnsNull_WhenNoSpacing()
    {
        Assert.Null(PixelSpacing.TryRead(TestDicom.Gradient(4, 4)));
    }

    [Fact]
    public void Distance_UsesColumnSpacingHorizontally_AndRowSpacingVertically()
    {
        // 行の間隔(縦) 2mm、列の間隔(横) 1mm: 横 30px = 30mm、縦 20px = 40mm → 斜辺 50mm
        var spacing = new PixelSpacing(Row: 2.0, Column: 1.0, PixelSpacingSource.PixelSpacing);

        Assert.Equal(50.0, spacing.Distance(10, 10, 40, 30), 6);
    }
}

public class DicomTagListerTests
{
    [Fact]
    public void List_ShowsStringAndNumericValues_AndSummarizesPixelData()
    {
        var entries = DicomTagLister.List(TestDicom.Gradient(5, 3));

        Assert.Contains(entries, e => e.Tag == "(0010,0010)" && e.Value == "Yamada^Taro");
        Assert.Contains(entries, e => e.Tag == "(0028,0010)" && e.Vr == "US" && e.Value == "3"); // Rows
        Assert.Contains(entries, e => e.Tag == "(0028,0011)" && e.Value == "5");                 // Columns
        var pixelData = Assert.Single(entries, e => e.Tag == "(7FE0,0010)");
        Assert.StartsWith("<画像データ", pixelData.Value);
    }

    [Fact]
    public void List_ExpandsSequencesWithIndentation()
    {
        var ds = TestDicom.Gradient(2, 2);
        ds.Add(new DicomSequence(DicomTag.ReferencedImageSequence,
            new DicomDataset { { DicomTag.ReferencedSOPInstanceUID, "1.2.3.4" } }));

        var entries = DicomTagLister.List(ds);

        var sequence = Assert.Single(entries, e => e.Tag == "(0008,1140)");
        Assert.Equal("(1 items)", sequence.Value);
        var child = Assert.Single(entries, e => e.Tag == "(0008,1155)");
        Assert.Equal("1.2.3.4", child.Value);
        Assert.True(child.Depth > sequence.Depth);
    }
}

public class DicomSeriesScannerTests
{
    private static string WriteDicom(string dir, string name, string seriesUid, int instance, int? seriesNumber = 1)
    {
        var ds = TestDicom.Gradient(2, 2);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        ds.AddOrUpdate(DicomTag.InstanceNumber, instance);
        if (seriesNumber is int n) ds.AddOrUpdate(DicomTag.SeriesNumber, n);
        ds.AddOrUpdate(DicomTag.SeriesDescription, $"Series {seriesUid}");
        string path = Path.Combine(dir, name);
        new DicomFile(ds).Save(path);
        return path;
    }

    [Fact]
    public async Task ScanAsync_GroupsBySeries_AndSortsByInstanceNumber()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        WriteDicom(dir, "c.dcm", "1.2.840.1", instance: 3);
        WriteDicom(dir, "a.dcm", "1.2.840.1", instance: 1);
        WriteDicom(Path.Combine(dir, "sub"), "IMG0002", "1.2.840.1", instance: 2); // 拡張子なし
        WriteDicom(dir, "x.dcm", "1.2.840.2", instance: 1, seriesNumber: 2);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not dicom");
        File.WriteAllText(Path.Combine(dir, "broken.dcm"), "not dicom either");

        var files = DicomSeriesScanner.ExpandPaths(new[] { dir }).ToList();
        var result = await DicomSeriesScanner.ScanAsync(files, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(files, f => f.EndsWith("notes.txt"));
        Assert.Equal(2, result.Series.Count);
        var first = result.Series[0];
        Assert.Equal("1.2.840.1", first.SeriesUid);
        Assert.Equal(new[] { "a.dcm", "IMG0002", "c.dcm" }, first.Files.Select(Path.GetFileName));
        Assert.Single(result.SkippedFiles, f => f.EndsWith("broken.dcm"));
        Assert.Equal(4, result.ImageCount);
    }

    [Fact]
    public void LooksLikeDicom_DetectsPreambleWithoutExtension()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string dicom = WriteDicom(dir, "IMAGE1", "1.2.3", instance: 1);
        string text = Path.Combine(dir, "README");
        File.WriteAllText(text, new string('x', 300));

        Assert.True(DicomSeriesScanner.LooksLikeDicom(dicom));
        Assert.False(DicomSeriesScanner.LooksLikeDicom(text));
    }

    [Fact]
    public void Group_PutsUnnumberedImagesLast()
    {
        var instances = new[]
        {
            new DicomInstanceInfo("b", "S", 1, "", "CT", null, null),
            new DicomInstanceInfo("a", "S", 1, "", "CT", 5, null),
            new DicomInstanceInfo("c", "S", 1, "", "CT", 1, null),
        };

        var series = Assert.Single(DicomSeriesScanner.Group(instances));

        Assert.Equal(new[] { "c", "a", "b" }, series.Files);
    }
}

public class WindowPresetTests
{
    [Fact]
    public void All_StartsWithImageDefault_AndHasPositiveWidths()
    {
        Assert.True(WindowPreset.All[0].IsImageDefault);
        Assert.All(WindowPreset.All.Skip(1), p => Assert.True(p.Width > 0));
    }
}
