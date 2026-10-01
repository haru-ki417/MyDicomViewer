using Velopack;

namespace MyDicomViewer;

/// <summary>アプリの入口</summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // インストール・更新・アンインストール時の処理（Velopack）。必ず最初に1回だけ呼ぶ。
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
