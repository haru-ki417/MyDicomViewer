using MyDicomViewer.Core.Configuration;

namespace MyDicomViewer.Tests;

public class AppConfigLayeringTests
{
    [Fact]
    public void Load_UserSettingsOverrideLocalFile()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, AppConfig.LocalSettingsFileName),
            """{ "OpenAI": { "Model": "from-file", "ApiKey": "file-key" } }""");

        var config = AppConfig.Load(dir, new Dictionary<string, string?> { [AppConfig.Keys.OpenAIModel] = "from-settings" });

        Assert.Equal("from-settings", config.OpenAIModel); // ユーザー設定が優先
        Assert.Equal("file-key", config.OpenAIApiKey);     // ユーザー設定に無い項目はファイルの値
    }

    [Fact]
    public void Load_WithoutAnySource_UsesDefaults_AndReportsMissingKeys()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;

        var config = AppConfig.Load(dir);

        Assert.Equal("gpt-4o-mini", config.OpenAIModel);
        Assert.StartsWith("https://github.com/", config.UpdateRepository);
        Assert.False(config.IsSet(AppConfig.Keys.CosmosPrimaryKey));
        Assert.Throws<MissingConfigurationException>(() => config.CosmosPrimaryKey);
    }
}
