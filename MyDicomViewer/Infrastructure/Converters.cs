using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MyDicomViewer.Infrastructure;

/// <summary>
/// 0〜1 の割合を、Grid の列幅（比率指定）に変換する。
/// ConverterParameter="Remainder" なら残りの割合（1 - 値）にする。確率スケールの表示に使う。
/// </summary>
public sealed class FractionToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double fraction = value is double d ? Math.Clamp(d, 0, 1) : 0;
        if (parameter as string == "Remainder") fraction = 1 - fraction;
        return new GridLength(fraction, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true なら非表示、false なら表示（「まだ何もないとき」の案内用）</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
