using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace MyDicomViewer.Views;

/// <summary>バージョン・著作権・AI モデル・ライセンスの表示</summary>
public partial class AboutWindow : Window
{
    public AboutWindow(string version, string modelInfo, string repositoryUrl)
    {
        InitializeComponent();

        VersionText.Text = $"バージョン {version}";
        CopyrightText.Text = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
        ModelText.Text = modelInfo;

        string repository = repositoryUrl.TrimEnd('/');
        RepositoryLink.NavigateUri = new Uri(repository);
        ModelCardLink.NavigateUri = new Uri($"{repository}/blob/main/docs/MODEL_CARD.md");
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
