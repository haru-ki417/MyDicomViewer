using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyDicomViewer.Core.Configuration;

namespace MyDicomViewer.Infrastructure;

/// <summary>アプリの設定画面で入力する値</summary>
public sealed class UserSettings
{
    // ---- 秘密情報（保存時に暗号化）
    public string? BlobConnectionString { get; set; }
    public string? CosmosPrimaryKey { get; set; }
    public string? OpenAIApiKey { get; set; }

    // ---- 通常の設定
    public string? CosmosEndpointUri { get; set; }
    public string? OpenAIModel { get; set; }
    public string? ModelDirectory { get; set; }
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>同意した利用上の注意の版。版が上がったら初回ガイドを再表示する。</summary>
    public string? AcceptedDisclaimerVersion { get; set; }

    /// <summary>AppConfig に渡す形（空の項目は渡さない＝下位の設定を使う）</summary>
    public IEnumerable<KeyValuePair<string, string?>> ToConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            [AppConfig.Keys.BlobConnectionString] = BlobConnectionString,
            [AppConfig.Keys.CosmosEndpointUri] = CosmosEndpointUri,
            [AppConfig.Keys.CosmosPrimaryKey] = CosmosPrimaryKey,
            [AppConfig.Keys.OpenAIApiKey] = OpenAIApiKey,
            [AppConfig.Keys.OpenAIModel] = OpenAIModel,
            [AppConfig.Keys.ModelDirectory] = ModelDirectory,
        };
        return values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value));
    }
}

/// <summary>
/// ユーザー設定を %LOCALAPPDATA%\MyDicomViewer\settings.json に保存する。
/// API キーなどの秘密情報は Windows の DPAPI で暗号化し、同じ Windows ユーザーでしか復号できないようにする。
/// </summary>
public sealed class UserSettingsStore
{
    private const string ProtectedPrefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyDicomViewer.UserSettings.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public UserSettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(AppPaths.DataDirectory, "settings.json");
    }

    public string FilePath { get; }

    public UserSettings Load()
    {
        if (!File.Exists(FilePath)) return new UserSettings();
        try
        {
            var stored = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
            stored.BlobConnectionString = Unprotect(stored.BlobConnectionString);
            stored.CosmosPrimaryKey = Unprotect(stored.CosmosPrimaryKey);
            stored.OpenAIApiKey = Unprotect(stored.OpenAIApiKey);
            return stored;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            // 壊れている・別ユーザーの設定などで読めない場合は初期状態として扱う
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        var stored = new UserSettings
        {
            BlobConnectionString = Protect(settings.BlobConnectionString),
            CosmosPrimaryKey = Protect(settings.CosmosPrimaryKey),
            OpenAIApiKey = Protect(settings.OpenAIApiKey),
            CosmosEndpointUri = settings.CosmosEndpointUri,
            OpenAIModel = settings.OpenAIModel,
            ModelDirectory = settings.ModelDirectory,
            CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup,
            AcceptedDisclaimerVersion = settings.AcceptedDisclaimerVersion,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(stored, JsonOptions));
        File.Move(temp, FilePath, overwrite: true); // 書き込み途中で落ちても壊れないように置き換える
    }

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(encrypted);
    }

    private static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return null;
        byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }
}
