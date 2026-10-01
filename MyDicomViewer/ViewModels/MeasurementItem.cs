using CommunityToolkit.Mvvm.ComponentModel;

namespace MyDicomViewer.ViewModels;

/// <summary>マウスで操作するときの道具</summary>
public enum ViewerTool
{
    /// <summary>左ドラッグで画像を移動</summary>
    Pan,

    /// <summary>左ドラッグで距離を計測</summary>
    Measure,
}

/// <summary>画像上の距離計測1本分（座標は画像の画素単位）</summary>
public partial class MeasurementItem : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LabelX), nameof(LabelY))] private double _x1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LabelX), nameof(LabelY))] private double _y1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LabelX), nameof(LabelY))] private double _x2;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LabelX), nameof(LabelY))] private double _y2;
    [ObservableProperty] private string _label = "";

    /// <summary>ラベルは線の中点の少し右下に置く</summary>
    public double LabelX => (X1 + X2) / 2 + 8;
    public double LabelY => (Y1 + Y2) / 2 + 8;

    public double LengthInPixels => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}
