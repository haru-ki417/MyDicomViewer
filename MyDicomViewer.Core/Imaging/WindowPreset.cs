namespace MyDicomViewer.Core.Imaging;

/// <summary>ウィンドウ（表示する濃度の範囲）の定番設定</summary>
public sealed record WindowPreset(string Name, double Center, double Width)
{
    /// <summary>画像に記録された推奨値に戻す特別な項目かどうか</summary>
    public bool IsImageDefault => double.IsNaN(Center);

    public static WindowPreset ImageDefault { get; } = new("画像の既定値", double.NaN, double.NaN);

    /// <summary>CT（単位: HU）でよく使う設定</summary>
    public static IReadOnlyList<WindowPreset> All { get; } = new[]
    {
        ImageDefault,
        new WindowPreset("肺野 (CT)", -600, 1500),
        new WindowPreset("縦隔 (CT)", 40, 400),
        new WindowPreset("腹部 (CT)", 40, 350),
        new WindowPreset("骨 (CT)", 400, 1800),
        new WindowPreset("脳 (CT)", 40, 80),
    };

    public override string ToString() => IsImageDefault ? Name : $"{Name}  L {Center:F0} / W {Width:F0}";
}
