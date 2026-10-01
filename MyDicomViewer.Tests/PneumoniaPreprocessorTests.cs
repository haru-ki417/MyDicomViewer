using MyDicomViewer.Core.Ai;

namespace MyDicomViewer.Tests;

public class PneumoniaPreprocessorTests
{
    [Fact]
    public void ToGrayscale_ProducesModelInputSize()
    {
        var ds = TestDicom.Gradient(300, 260);

        byte[] gray = PneumoniaPreprocessor.ToGrayscale(ds);

        Assert.Equal(PneumoniaPreprocessor.InputSize * PneumoniaPreprocessor.InputSize, gray.Length);
        Assert.Equal(0, gray.Min());
        Assert.Equal(255, gray.Max());
    }

    [Fact]
    public void ToGrayscale_Monochrome1_IsInvertedRelativeToMonochrome2()
    {
        byte[] normal = PneumoniaPreprocessor.ToGrayscale(TestDicom.Gradient(8, 8, "MONOCHROME2"), size: 8);
        byte[] inverted = PneumoniaPreprocessor.ToGrayscale(TestDicom.Gradient(8, 8, "MONOCHROME1"), size: 8);

        for (int i = 0; i < normal.Length; i++)
        {
            Assert.InRange(normal[i] + inverted[i], 254, 256); // 丸め誤差 ±1 を許容
        }
    }

    [Fact]
    public void ToGrayscale_IsUnaffectedByPositiveLinearRescale()
    {
        // min-max 正規化するので、正の傾きの一次変換（Rescale）では結果が変わらない
        byte[] plain = PneumoniaPreprocessor.ToGrayscale(TestDicom.Gradient(16, 16), size: 8);
        byte[] rescaled = PneumoniaPreprocessor.ToGrayscale(TestDicom.Gradient(16, 16, slope: 2m, intercept: -1024m), size: 8);

        Assert.Equal(plain, rescaled);
    }

    [Fact]
    public void ToInputTensor_AppliesImageNetNormalizationPerChannel()
    {
        var gray = new byte[] { 0, 255 };

        float[] input = PneumoniaPreprocessor.ToInputTensor(gray);

        Assert.Equal(6, input.Length);
        Assert.Equal((0f - 0.485f) / 0.229f, input[0], 5); // R, 黒
        Assert.Equal((1f - 0.485f) / 0.229f, input[1], 5); // R, 白
        Assert.Equal((0f - 0.456f) / 0.224f, input[2], 5); // G, 黒
        Assert.Equal((1f - 0.406f) / 0.225f, input[5], 5); // B, 白
    }

    [Fact]
    public void ModelMetadata_ReadsThreshold_AndFallsBackWhenMissing()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(dir, "model_meta.json");
        File.WriteAllText(path, """{ "model": "efficientnet_b0", "threshold": 0.5409 }""");

        var loaded = ModelMetadata.Load(path);
        var missing = ModelMetadata.Load(Path.Combine(dir, "none.json"));

        Assert.Equal(0.5409f, loaded.Threshold, 4);
        Assert.Equal("efficientnet_b0", loaded.ModelName);
        Assert.Equal(ModelMetadata.DefaultThreshold, missing.Threshold);
    }

    [Fact]
    public void PneumoniaResult_IsPositive_WhenProbabilityReachesThreshold()
    {
        Assert.True(new PneumoniaResult(0.60f, 0.54f, Array.Empty<float>(), 0, TimeSpan.Zero).IsPositive);
        Assert.True(new PneumoniaResult(0.54f, 0.54f, Array.Empty<float>(), 0, TimeSpan.Zero).IsPositive);
        Assert.False(new PneumoniaResult(0.53f, 0.54f, Array.Empty<float>(), 0, TimeSpan.Zero).IsPositive);
    }
}
