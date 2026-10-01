using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyDicomViewer.ViewModels;

namespace MyDicomViewer.Views;

/// <summary>
/// 画像表示部分。マウス操作を座標に変換して ViewModel に渡す（見た目と操作だけを担当）。
///   ホイール: スライス送り（1枚だけの画像では拡大縮小）
///   Ctrl + ホイール: カーソル位置を中心に拡大縮小
///   左ドラッグ: 移動（計測モードでは距離の計測）
///   右ドラッグ: 濃度調整（横: ウィンドウ幅 / 縦: ウィンドウ中心）
///   ダブルクリック: 表示を元に戻す
/// </summary>
public partial class DicomViewport : UserControl
{
    private const double ZoomStep = 1.15;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 30;

    private enum DragMode { None, Pan, Measure, Window }

    private DragMode _dragMode;
    private Point _lastPoint;
    private MainViewModel? _viewModel;

    public DicomViewport()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? ViewModel => _viewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.ResetViewRequested -= OnResetViewRequested;
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null) _viewModel.ResetViewRequested += OnResetViewRequested;
    }

    private void OnResetViewRequested(object? sender, EventArgs e) => ResetView();

    public void ResetView()
    {
        ViewTransform.Matrix = Matrix.Identity;
        if (ViewModel is not null) ViewModel.ZoomPercent = 100;
    }

    // ------------------------------------------------------------ ホイール
    private void Host_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasImage) return;

        bool zoom = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || ViewModel.SliceCount <= 1;
        if (zoom)
        {
            ZoomAt(e.GetPosition(Host), e.Delta > 0 ? ZoomStep : 1 / ZoomStep);
        }
        else
        {
            ViewModel.StepSlice(e.Delta > 0 ? -1 : +1);
        }
        e.Handled = true;
    }

    private void ZoomAt(Point center, double factor)
    {
        var matrix = ViewTransform.Matrix;
        double newScale = matrix.M11 * factor;
        if (newScale < MinZoom || newScale > MaxZoom) return;

        // カーソル位置が動かないように拡大する
        matrix.ScaleAt(factor, factor, center.X, center.Y);
        ViewTransform.Matrix = matrix;
        if (ViewModel is not null) ViewModel.ZoomPercent = newScale * 100;
    }

    // ------------------------------------------------------------ 左ボタン（移動・計測）
    private void Host_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Host.Focus();
        if (ViewModel is null || !ViewModel.HasImage) return;

        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        if (ViewModel.ActiveTool == ViewerTool.Measure)
        {
            var p = e.GetPosition(ImageLayer); // 画像の画素座標（拡大・移動を考慮済み）
            ViewModel.BeginMeasurement(p.X, p.Y);
            _dragMode = DragMode.Measure;
        }
        else
        {
            _lastPoint = e.GetPosition(Host);
            _dragMode = DragMode.Pan;
            Cursor = Cursors.SizeAll;
        }
        Host.CaptureMouse();
        e.Handled = true;
    }

    private void Host_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    // ------------------------------------------------------------ 右ボタン（濃度調整）
    private void Host_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null || !ViewModel.HasImage) return;
        _lastPoint = e.GetPosition(Host);
        _dragMode = DragMode.Window;
        Cursor = Cursors.Cross;
        Host.CaptureMouse();
        e.Handled = true;
    }

    private void Host_MouseRightButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void Host_MouseMove(object sender, MouseEventArgs e)
    {
        if (ViewModel is null || _dragMode == DragMode.None) return;

        switch (_dragMode)
        {
            case DragMode.Pan:
            {
                var p = e.GetPosition(Host);
                var matrix = ViewTransform.Matrix;
                matrix.Translate(p.X - _lastPoint.X, p.Y - _lastPoint.Y);
                ViewTransform.Matrix = matrix;
                _lastPoint = p;
                break;
            }
            case DragMode.Measure:
            {
                var p = e.GetPosition(ImageLayer);
                ViewModel.UpdateMeasurement(p.X, p.Y);
                break;
            }
            case DragMode.Window:
            {
                var p = e.GetPosition(Host);
                ViewModel.AdjustWindow(p.X - _lastPoint.X, p.Y - _lastPoint.Y);
                _lastPoint = p;
                break;
            }
        }
    }

    private void Host_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_dragMode == DragMode.Measure) ViewModel?.EndMeasurement();
        _dragMode = DragMode.None;
        Cursor = null;
        if (Host.IsMouseCaptured) Host.ReleaseMouseCapture();
    }

    // ------------------------------------------------------------ ドラッグ＆ドロップ
    private void Host_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Host_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        await ViewModel.OpenPathsAsync(paths.Where(p => File.Exists(p) || Directory.Exists(p)));
    }
}
