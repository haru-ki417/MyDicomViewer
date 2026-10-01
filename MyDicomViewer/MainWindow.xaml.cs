using System.Windows;
using MyDicomViewer.ViewModels;

namespace MyDicomViewer;

/// <summary>画面の見た目だけを持つ。処理はすべて MainViewModel にある。</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
