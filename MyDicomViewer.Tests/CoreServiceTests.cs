using FellowOakDicom;
using Microsoft.Extensions.Configuration;
using MyDicomViewer.Core.Cloud;
using MyDicomViewer.Core.Configuration;
using MyDicomViewer.Core.Dicom;
using MyDicomViewer.Core.Imaging;

namespace MyDicomViewer.Tests;

public class DicomDeidentifierTests
{
    [Fact]
    public void Deidentify_RemovesPersonalInformation_AndAssignsPseudonym()
    {
        var original = TestDicom.Gradient(4, 4);

        var result = DicomDeidentifier.Deidentify(original);
        var ds = result.Dataset;

        Assert.Equal(DicomDeidentifier.AnonymousPatientName, ds.GetString(DicomTag.PatientName));
        Assert.Matches("^ID-[0-9a-f]{8}$", ds.GetString(DicomTag.PatientID));
        Assert.Equal(result.PseudonymId, ds.GetString(DicomTag.PatientID));
        Assert.Equal("YES", ds.GetString(DicomTag.PatientIdentityRemoved));
        Assert.NotEqual("19800101", ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, ""));
        Assert.NotEqual("Suzuki^Ichiro", ds.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, ""));
        Assert.NotEqual("Example Hospital", ds.GetSingleValueOrDefault(DicomTag.InstitutionName, ""));
        Assert.True(ds.Contains(DicomTag.PixelData)); // 画像そのものは残す
    }

    [Fact]
    public void Deidentify_DoesNotModifyOriginal()
    {
        var original = TestDicom.Gradient(4, 4);

        DicomDeidentifier.Deidentify(original);

        Assert.Equal("Yamada^Taro", original.GetString(DicomTag.PatientName));
        Assert.Equal("PAT-12345", original.GetString(DicomTag.PatientID));
    }

    [Fact]
    public void Deidentify_GeneratesDifferentPseudonymEachTime()
    {
        var original = TestDicom.Gradient(4, 4);

        Assert.NotEqual(
            DicomDeidentifier.Deidentify(original).PseudonymId,
            DicomDeidentifier.Deidentify(original).PseudonymId);
    }
}

public class CloudQueryTests
{
    [Fact]
    public void BuildSearchQuery_PassesKeywordAsParameter_NotInQueryText()
    {
        const string malicious = "x') OR 1=1 --";

        var query = AzureCloudArchive.BuildSearchQuery(malicious);

        Assert.DoesNotContain(malicious, query.QueryText);
        var parameter = Assert.Single(query.GetQueryParameters());
        Assert.Equal("@keyword", parameter.Name);
        Assert.Equal(malicious, parameter.Value);
    }

    [Fact]
    public void BuildSearchQuery_EmptyKeyword_ReturnsAll()
    {
        var query = AzureCloudArchive.BuildSearchQuery("  ");

        Assert.Equal("SELECT * FROM c", query.QueryText);
        Assert.Empty(query.GetQueryParameters());
    }
}

public class AppConfigTests
{
    private static AppConfig Create(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Require_MissingKey_ThrowsWithKeyName()
    {
        var config = Create(new());

        var ex = Assert.Throws<MissingConfigurationException>(() => config.OpenAIApiKey);
        Assert.Equal("OpenAI:ApiKey", ex.Key);
        Assert.Contains("MYDICOMVIEWER_OpenAI__ApiKey", ex.Message);
    }

    [Fact]
    public void Require_PlaceholderValue_IsTreatedAsMissing()
    {
        var config = Create(new() { ["OpenAI:ApiKey"] = "<OpenAI の API キー>" });

        Assert.False(config.IsSet("OpenAI:ApiKey"));
        Assert.Throws<MissingConfigurationException>(() => config.OpenAIApiKey);
    }

    [Fact]
    public void Optional_UsesDefault_UnlessConfigured()
    {
        Assert.Equal("gpt-4o-mini", Create(new()).OpenAIModel);
        Assert.Equal("gpt-test", Create(new() { ["OpenAI:Model"] = "gpt-test" }).OpenAIModel);
    }
}

public class HeatmapRendererTests
{
    [Fact]
    public void Render_ZeroAttention_IsFullyTransparent()
    {
        var heatmap = HeatmapRenderer.Render(new float[4 * 4], 4, 20, 10);

        for (int i = 3; i < heatmap.Bgra.Length; i += 4) Assert.Equal(0, heatmap.Bgra[i]);
    }

    [Fact]
    public void Render_StrongAttention_IsOpaqueRed()
    {
        var heatmap = HeatmapRenderer.Render(Enumerable.Repeat(1f, 4 * 4).ToArray(), 4, 10, 10);

        Assert.Equal(255, heatmap.Bgra[3]); // A
        Assert.Equal(128, heatmap.Bgra[2]); // R（JET の最大値は暗めの赤）
        Assert.Equal(0, heatmap.Bgra[1]);   // G
        Assert.Equal(0, heatmap.Bgra[0]);   // B
    }

    [Fact]
    public void Render_LargeImage_IsDownscaledKeepingAspectRatio()
    {
        var heatmap = HeatmapRenderer.Render(new float[2 * 2], 2, 4000, 2000, maxSide: 1000);

        Assert.Equal(1000, heatmap.Width);
        Assert.Equal(500, heatmap.Height);
    }
}
