using Microsoft.Extensions.Configuration;

namespace MyDicomViewer.Core.Configuration;

/// <summary>
/// 接続文字列・API キー・各種パスをソースコードの外から読み込む。
/// 優先順位（後ろほど優先）:
///   1. appsettings.Local.json … 開発用のローカル設定（Git 管理外。配布物には含めない）
///   2. ユーザー設定 … アプリの設定画面で入力した値（Windows のユーザーごとに暗号化して保存）
///   3. 環境変数 MYDICOMVIEWER_ で始まるもの（例: MYDICOMVIEWER_OpenAI__ApiKey）
/// </summary>
public sealed class AppConfig
{
    public const string EnvironmentPrefix = "MYDICOMVIEWER_";
    public const string LocalSettingsFileName = "appsettings.Local.json";

    private readonly IConfiguration _configuration;

    public AppConfig(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>実行フォルダの設定ファイル・ユーザー設定・環境変数から読み込む</summary>
    public static AppConfig Load(string baseDirectory, IEnumerable<KeyValuePair<string, string?>>? userSettings = null)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile(LocalSettingsFileName, optional: true, reloadOnChange: false)
            .AddInMemoryCollection(userSettings ?? Array.Empty<KeyValuePair<string, string?>>())
            .AddEnvironmentVariables(EnvironmentPrefix)
            .Build();
        return new AppConfig(configuration);
    }

    // ---- 設定のキー（設定画面・テストと共通）
    public static class Keys
    {
        public const string BlobConnectionString = "Azure:BlobConnectionString";
        public const string CosmosEndpointUri = "Azure:CosmosEndpointUri";
        public const string CosmosPrimaryKey = "Azure:CosmosPrimaryKey";
        public const string OpenAIApiKey = "OpenAI:ApiKey";
        public const string OpenAIModel = "OpenAI:Model";
        public const string ModelDirectory = "Ai:ModelDirectory";
        public const string UpdateRepository = "Updates:GitHubRepository";
    }

    // ---- 必須（未設定なら MissingConfigurationException）
    public string BlobConnectionString => Require(Keys.BlobConnectionString);
    public string CosmosEndpointUri => Require(Keys.CosmosEndpointUri);
    public string CosmosPrimaryKey => Require(Keys.CosmosPrimaryKey);
    public string OpenAIApiKey => Require(Keys.OpenAIApiKey);

    // ---- 任意（既定値あり）
    public string BlobContainerName => Optional("Azure:BlobContainerName", "dicom-images");
    public string CosmosDatabaseId => Optional("Azure:CosmosDatabaseId", "DicomDb");
    public string CosmosContainerId => Optional("Azure:CosmosContainerId", "Records");
    public string OpenAIModel => Optional(Keys.OpenAIModel, "gpt-4o-mini");
    public string ModelDirectory => Optional(Keys.ModelDirectory, Path.Combine(AppContext.BaseDirectory, "Models"));
    public string UpdateRepository => Optional(Keys.UpdateRepository, "https://github.com/haru-ki417/MyDicomViewer");

    /// <summary>値が設定済みか（プレースホルダーのままは未設定とみなす）</summary>
    public bool IsSet(string key) => IsUsable(_configuration[key]);

    public string Require(string key)
    {
        string? value = _configuration[key];
        if (!IsUsable(value)) throw new MissingConfigurationException(key);
        return value!;
    }

    public string Optional(string key, string defaultValue)
    {
        string? value = _configuration[key];
        return IsUsable(value) ? value! : defaultValue;
    }

    private static bool IsUsable(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.TrimStart().StartsWith('<');
}

/// <summary>必要な設定が無いときの例外。画面に出しても安全なメッセージを持つ。</summary>
public sealed class MissingConfigurationException : InvalidOperationException
{
    public string Key { get; }

    public MissingConfigurationException(string key)
        : base($"設定「{key}」がありません。\n" +
               "メニューの「ツール」→「設定」で入力するか、" +
               $"環境変数 {AppConfig.EnvironmentPrefix}{key.Replace(":", "__")} を設定してください。")
    {
        Key = key;
    }
}
