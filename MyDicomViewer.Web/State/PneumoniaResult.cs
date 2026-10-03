namespace MyDicomViewer.Core.Ai;

// Windows 版では PneumoniaScreeningService.cs にある型（ONNX Runtime を使うファイルなので、ブラウザー版では型だけを置く）

/// <summary>AI スクリーニングの結果（確率・判定閾値・Grad-CAM）</summary>
public sealed record PneumoniaResult(float Probability, float Threshold, float[] Cam, int CamSize, TimeSpan Elapsed, string ModelName = "unknown")
{
    public bool IsPositive => Probability >= Threshold;
}
