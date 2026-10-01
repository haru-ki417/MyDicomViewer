using Microsoft.Extensions.Logging;
using MyDicomViewer.Core.Configuration;
using Velopack;
using Velopack.Sources;

namespace MyDicomViewer.Services;

public sealed record UpdateCheckResult(bool IsSupported, string? NewVersion, UpdateInfo? Info);

/// <summary>GitHub Releases に公開した新しい版を確認・適用する（Velopack）</summary>
public interface IUpdateService
{
    /// <summary>インストーラーで導入された場合だけ true（Visual Studio から実行中は false）</summary>
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    Task<UpdateCheckResult> CheckAsync();
    Task DownloadAndRestartAsync(UpdateInfo update, IProgress<int>? progress = null);
}

public sealed class UpdateService : IUpdateService
{
    private readonly UpdateManager _manager;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(AppConfig config, ILogger<UpdateService> logger)
    {
        _logger = logger;
        _manager = new UpdateManager(new GithubSource(config.UpdateRepository, null, false));
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";

    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (!IsInstalled) return new UpdateCheckResult(false, null, null);

        var info = await _manager.CheckForUpdatesAsync();
        _logger.LogInformation("Update check: {Result}", info?.TargetFullRelease.Version.ToString() ?? "up to date");
        return new UpdateCheckResult(true, info?.TargetFullRelease.Version.ToString(), info);
    }

    public async Task DownloadAndRestartAsync(UpdateInfo update, IProgress<int>? progress = null)
    {
        await _manager.DownloadUpdatesAsync(update, p => progress?.Report(p));
        _logger.LogInformation("Applying update {Version} and restarting", update.TargetFullRelease.Version);
        _manager.ApplyUpdatesAndRestart(update);
    }
}
