using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyDicomViewer.Core.Ai;
using MyDicomViewer.Core.Cloud;
using MyDicomViewer.Core.Configuration;
using MyDicomViewer.Core.Reports;
using MyDicomViewer.Infrastructure;
using MyDicomViewer.Services;
using MyDicomViewer.ViewModels;

namespace MyDicomViewer;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 同意画面などを閉じてもアプリが終了しないよう、メイン画面を出すまでは明示的に終了させる
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // すべてのウィンドウのタイトルバーをアプリの配色に合わせる
        DarkTitleBar.RegisterForAllWindows();

        // fo-dicom の初期化: WPF 用の画像描画と、圧縮 DICOM（JPEG など）用のネイティブコーデック
        new DicomSetupBuilder()
            .RegisterServices(s => s.AddFellowOakDicom()
                .AddTranscoderManager<FellowOakDicom.Imaging.NativeCodec.NativeTranscoderManager>()
                .AddImageManager<WPFImageManager>())
            .SkipValidation()
            .Build();

        var settingsStore = new UserSettingsStore();
        var settings = settingsStore.Load();

        _services = ConfigureServices(settingsStore, settings).BuildServiceProvider();
        _logger = _services.GetRequiredService<ILogger<App>>();
        RegisterGlobalExceptionHandlers();
        _logger.LogInformation("Application started (version {Version})", typeof(App).Assembly.GetName().Version);

        var shell = _services.GetRequiredService<AppShell>();
        if (!shell.EnsureDisclaimerAccepted())
        {
            _logger.LogInformation("Disclaimer declined; exiting");
            Shutdown();
            return;
        }

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();

        if (settings.CheckForUpdatesOnStartup) _ = CheckForUpdatesLaterAsync(shell);
    }

    private static ServiceCollection ConfigureServices(UserSettingsStore settingsStore, UserSettings settings)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(AppPaths.LogDirectory)));

        services.AddSingleton(settingsStore);
        services.AddSingleton(_ => AppConfig.Load(AppContext.BaseDirectory, settings.ToConfiguration()));

        // ---- Core（画面に依存しない処理）
        services.AddSingleton<ICloudArchive, AzureCloudArchive>();
        services.AddSingleton<IReportGenerator, OpenAiReportGenerator>();
        services.AddSingleton<IPneumoniaScreeningService>(sp => new PneumoniaScreeningService(
            sp.GetRequiredService<AppConfig>().ModelDirectory,
            sp.GetRequiredService<ILogger<PneumoniaScreeningService>>()));

        // ---- アプリ全体の操作・更新
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<AppShell>();
        services.AddSingleton<IAppShell>(sp => sp.GetRequiredService<AppShell>());

        // ---- 画面
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IImageRenderer, ImageRenderer>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }

    /// <summary>起動直後の操作を妨げないよう、少し待ってから更新を確認する（最新なら何も表示しない）</summary>
    private static async Task CheckForUpdatesLaterAsync(AppShell shell)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        await shell.CheckForUpdatesAsync(quietWhenUpToDate: true);
    }

    /// <summary>想定外の例外でアプリが無言で落ちないように、記録してから利用者に知らせる</summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating={Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"予期しないエラーが発生しました。作業は続けられます。\n\n{e.Exception.Message}\n\n詳細はログに記録しました:\n{AppPaths.LogDirectory}",
            "MyDicomViewer", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Application exiting");
        _services?.Dispose();
        base.OnExit(e);
    }
}
