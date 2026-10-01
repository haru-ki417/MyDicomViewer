using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyDicomViewer.Core.Ai;
using MyDicomViewer.Core.Configuration;
using MyDicomViewer.Infrastructure;
using MyDicomViewer.Views;

namespace MyDicomViewer.Services;

/// <summary>メニューから呼ぶアプリ全体の操作（設定・ガイド・バージョン情報・更新・終了）</summary>
public interface IAppShell
{
    void OpenSettings();
    void ShowWelcome();
    void ShowAbout();
    Task CheckForUpdatesAsync(bool quietWhenUpToDate);
    void OpenLogFolder();
    void Exit();
}

public sealed class AppShell : IAppShell
{
    /// <summary>利用上の注意の版。文言を大きく変えたら上げると、全員に初回ガイドを再表示する。</summary>
    public const string DisclaimerVersion = "1";

    private readonly UserSettingsStore _store;
    private readonly IUpdateService _updates;
    private readonly AppConfig _config;
    private readonly IPneumoniaScreeningService _screening;
    private readonly ILogger<AppShell> _logger;

    public AppShell(UserSettingsStore store, IUpdateService updates, AppConfig config,
        IPneumoniaScreeningService screening, ILogger<AppShell> logger)
    {
        _store = store;
        _updates = updates;
        _config = config;
        _screening = screening;
        _logger = logger;
    }

    private static Window? Owner => Application.Current.MainWindow is { IsLoaded: true } w ? w : null;

    /// <summary>初回起動時（または注意事項の版が上がったとき）に、同意を得るまで先に進めない</summary>
    public bool EnsureDisclaimerAccepted()
    {
        var settings = _store.Load();
        if (settings.AcceptedDisclaimerVersion == DisclaimerVersion) return true;

        var window = new WelcomeWindow(requireAcceptance: true);
        if (window.ShowDialog() != true) return false;

        settings.AcceptedDisclaimerVersion = DisclaimerVersion;
        _store.Save(settings);
        _logger.LogInformation("Disclaimer version {Version} accepted", DisclaimerVersion);
        return true;
    }

    public void ShowWelcome() => new WelcomeWindow(requireAcceptance: false) { Owner = Owner }.ShowDialog();

    public void OpenSettings()
    {
        var window = new SettingsWindow(_store.Load(), _config.ModelDirectory) { Owner = Owner };
        if (window.ShowDialog() != true) return;

        _store.Save(window.Result);
        _logger.LogInformation("User settings saved");

        // 接続先などはアプリ起動時に組み立てるため、反映には再起動が必要
        if (MessageBox.Show("設定を保存しました。\n反映するにはアプリの再起動が必要です。今すぐ再起動しますか？",
                "設定", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            Restart();
        }
    }

    public void ShowAbout()
    {
        string modelPath = Path.Combine(_screening.ModelDirectory, PneumoniaScreeningService.ModelFileName);
        string modelInfo;
        if (_screening.IsModelAvailable)
        {
            var meta = ModelMetadata.Load(Path.Combine(_screening.ModelDirectory, PneumoniaScreeningService.MetadataFileName));
            modelInfo = $"{meta.ModelName}（判定閾値 {meta.Threshold:F3}）\n{modelPath}";
        }
        else
        {
            modelInfo = $"未配置\n{modelPath}";
        }

        new AboutWindow(_updates.CurrentVersion, modelInfo, _config.UpdateRepository) { Owner = Owner }.ShowDialog();
    }

    public async Task CheckForUpdatesAsync(bool quietWhenUpToDate)
    {
        try
        {
            var result = await _updates.CheckAsync();
            if (!result.IsSupported)
            {
                if (!quietWhenUpToDate)
                {
                    MessageBox.Show("自動更新は、インストーラーで導入したアプリでのみ利用できます。\n（開発環境から実行している場合は対象外です）",
                        "更新の確認", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            if (result.Info is null)
            {
                if (!quietWhenUpToDate)
                {
                    MessageBox.Show($"最新の版です（{_updates.CurrentVersion}）。", "更新の確認", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            if (MessageBox.Show($"新しい版 {result.NewVersion} があります（現在 {_updates.CurrentVersion}）。\n今すぐ更新して再起動しますか？",
                    "更新の確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _updates.DownloadAndRestartAsync(result.Info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            if (!quietWhenUpToDate)
            {
                MessageBox.Show($"更新の確認に失敗しました。ネットワーク接続を確認してください。\n\n{ex.Message}",
                    "更新の確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.LogDirectory) { UseShellExecute = true });
    }

    public void Exit() => Application.Current.Shutdown();

    private static void Restart()
    {
        string? exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
