using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FellowOakDicom.Imaging;
using Microsoft.Win32;
using MyDicomViewer.Core.Dicom;
using MyDicomViewer.Core.Imaging;
using MyDicomViewer.Views;

namespace MyDicomViewer.Services;

/// <summary>ダイアログ表示（ViewModel から画面部品を直接触らないための窓口）</summary>
public interface IDialogService
{
    IReadOnlyList<string> PickDicomFiles();
    string? PickFolder();
    string? PickSavePath(string defaultFileName);
    void ShowTags(string title, IReadOnlyList<DicomTagEntry> entries);
    void ShowInfo(string message, string title = "MyDicomViewer");
    void ShowError(string message, string title = "エラー");
    bool Confirm(string message, string title);
}

public sealed class DialogService : IDialogService
{
    public IReadOnlyList<string> PickDicomFiles()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "DICOM Files (*.dcm)|*.dcm|All Files (*.*)|*.*",
            Title = "DICOMファイルを選択してください（複数選択可）",
            Multiselect = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "DICOM ファイルが入ったフォルダを選択してください（サブフォルダも読み込みます）" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickSavePath(string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "DICOM Files (*.dcm)|*.dcm",
            Title = "AI の結果を DICOM として保存",
            FileName = defaultFileName,
            AddExtension = true,
            DefaultExt = ".dcm",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void ShowTags(string title, IReadOnlyList<DicomTagEntry> entries)
    {
        var window = new TagViewerWindow(title, entries) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    public void ShowInfo(string message, string title = "MyDicomViewer") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title = "エラー") =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}

/// <summary>DICOM 画像やヒートマップを WPF で表示できる形に変換する</summary>
public interface IImageRenderer
{
    BitmapSource Render(DicomImage image);
    BitmapSource ToBitmap(HeatmapPixels heatmap);
    byte[] EncodeJpeg(BitmapSource source);
}

public sealed class ImageRenderer : IImageRenderer
{
    public BitmapSource Render(DicomImage image) => image.RenderImage().As<WriteableBitmap>();

    public BitmapSource ToBitmap(HeatmapPixels heatmap)
    {
        var bitmap = new WriteableBitmap(heatmap.Width, heatmap.Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, heatmap.Width, heatmap.Height), heatmap.Bgra, heatmap.Width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    public byte[] EncodeJpeg(BitmapSource source)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
