using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MyDicomViewer.Core.Dicom;

namespace MyDicomViewer.Views;

/// <summary>DICOM タグを一覧・検索する画面（表示専用）</summary>
public partial class TagViewerWindow : Window
{
    private readonly ICollectionView _view;
    private readonly int _total;

    public TagViewerWindow(string title, IReadOnlyList<DicomTagEntry> entries)
    {
        InitializeComponent();
        Title = title;
        _total = entries.Count;
        _view = CollectionViewSource.GetDefaultView(entries);
        TagGrid.ItemsSource = _view;
        UpdateCount();
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string keyword = FilterBox.Text.Trim();
        _view.Filter = keyword.Length == 0
            ? null
            : item => item is DicomTagEntry entry &&
                      (entry.Tag.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                       entry.Tag.Replace(",", "").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                       entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                       entry.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        UpdateCount();
    }

    private void UpdateCount()
    {
        int shown = _view.Cast<object>().Count();
        CountText.Text = shown == _total ? $"{_total} 件" : $"{shown} / {_total} 件";
    }
}
