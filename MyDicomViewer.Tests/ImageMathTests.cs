using MyDicomViewer.Core.Imaging;

namespace MyDicomViewer.Tests;

public class ImageMathTests
{
    [Fact]
    public void ResizeArea_IntegerFactor_AveragesEachBlock()
    {
        float[] src = Enumerable.Range(0, 16).Select(i => (float)i).ToArray(); // 4x4: 0..15

        float[] dst = ImageMath.ResizeArea(src, 4, 4, 2, 2);

        Assert.Equal(new[] { 2.5f, 4.5f, 10.5f, 12.5f }, dst);
    }

    [Fact]
    public void ResizeArea_NonIntegerFactor_PreservesMeanBrightness()
    {
        var random = new Random(42);
        float[] src = Enumerable.Range(0, 7 * 5).Select(_ => (float)random.NextDouble()).ToArray();

        float[] dst = ImageMath.ResizeArea(src, 7, 5, 3, 2);

        Assert.Equal(src.Average(), dst.Average(), 5);
    }

    [Fact]
    public void Resize_UniformImage_StaysUniform_WhenEnlarged()
    {
        float[] src = Enumerable.Repeat(0.3f, 3 * 2).ToArray();

        float[] dst = ImageMath.Resize(src, 3, 2, 8, 5);

        Assert.All(dst, v => Assert.Equal(0.3f, v, 5));
    }

    [Fact]
    public void Resize_RejectsMismatchedPixelCount()
    {
        Assert.Throws<ArgumentException>(() => ImageMath.ResizeArea(new float[5], 2, 2, 1, 1));
    }

    [Fact]
    public void NormalizeMinMax_MapsMinToZeroAndMaxToOne()
    {
        float[] result = ImageMath.NormalizeMinMax(new float[] { 10, 20, 30 }, invert: false);

        Assert.Equal(new[] { 0f, 0.5f, 1f }, result);
    }

    [Fact]
    public void NormalizeMinMax_Invert_FlipsBrightness()
    {
        float[] result = ImageMath.NormalizeMinMax(new float[] { 10, 20, 30 }, invert: true);

        Assert.Equal(new[] { 1f, 0.5f, 0f }, result);
    }

    [Fact]
    public void NormalizeMinMax_ConstantImage_BecomesAllZero()
    {
        float[] result = ImageMath.NormalizeMinMax(new float[] { 7, 7, 7 }, invert: false);

        Assert.All(result, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ToUInt8_UsesRoundHalfToEven_LikeNumpy()
    {
        // 0.5 * 255 = 127.5 → numpy の round は 128（偶数丸め）
        byte[] result = ImageMath.ToUInt8(new[] { 0f, 0.5f, 1f, -0.2f, 1.3f });

        Assert.Equal(new byte[] { 0, 128, 255, 0, 255 }, result);
    }
}
