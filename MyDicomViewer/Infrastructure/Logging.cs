using System.IO;
using Microsoft.Extensions.Logging;

namespace MyDicomViewer.Infrastructure;

/// <summary>アプリが使うフォルダ</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyDicomViewer");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>クラウドから取得した DICOM の一時保存先</summary>
    public static string DownloadDirectory { get; } = Path.Combine(Path.GetTempPath(), "MyDicomViewer", "downloads");
}

/// <summary>
/// 日付ごとのファイルにログを書き出す最小限のロガー。
/// 障害調査用なので、患者を特定できる情報はログに書かない方針にしている。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // ログが書けなくてもアプリは止めない
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
