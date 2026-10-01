using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MyDicomViewer.Infrastructure;

namespace MyDicomViewer.Views;

/// <summary>
/// 設定画面。秘密情報は PasswordBox で入力し、画面には表示しない。
/// 空欄のまま保存した秘密情報は「変更しない」として扱う。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly UserSettings _original;

    public SettingsWindow(UserSettings current, string effectiveModelDirectory)
    {
        InitializeComponent();
        _original = current;

        ModelDirectoryBox.Text = current.ModelDirectory ?? "";
        CosmosEndpointBox.Text = current.CosmosEndpointUri ?? "";
        OpenAIModelBox.Text = current.OpenAIModel ?? "";
        CheckUpdatesBox.IsChecked = current.CheckForUpdatesOnStartup;

        SetSecretHint(BlobHint, current.BlobConnectionString);
        SetSecretHint(CosmosKeyHint, current.CosmosPrimaryKey);
        SetSecretHint(OpenAIKeyHint, current.OpenAIApiKey);
        OpenAIModelBox.ToolTip = "空欄なら gpt-4o-mini";

        bool modelFound = File.Exists(Path.Combine(effectiveModelDirectory, "pneumonia.onnx"));
        ModelHint.Text += modelFound
            ? $"\n現在: {effectiveModelDirectory}（モデルあり）"
            : $"\n現在: {effectiveModelDirectory}（モデルが見つかりません）";
    }

    /// <summary>保存ボタンで確定した設定</summary>
    public UserSettings Result { get; private set; } = new();

    private static void SetSecretHint(TextBlock hint, string? value) =>
        hint.Text = string.IsNullOrEmpty(value)
            ? "未設定"
            : $"設定済み（末尾 {Mask(value)}）。変更する場合だけ入力してください。空欄なら現在の値を維持します。";

    private static string Mask(string value) => value.Length <= 4 ? "****" : value[^4..];

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "pneumonia.onnx があるフォルダを選択" };
        if (dialog.ShowDialog() == true) ModelDirectoryBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string modelDir = ModelDirectoryBox.Text.Trim();
        if (modelDir.Length > 0 && !File.Exists(Path.Combine(modelDir, "pneumonia.onnx")) &&
            MessageBox.Show("指定したフォルダに pneumonia.onnx が見つかりません。このまま保存しますか？", "設定",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        Result = new UserSettings
        {
            ModelDirectory = NullIfEmpty(modelDir),
            CosmosEndpointUri = NullIfEmpty(CosmosEndpointBox.Text.Trim()),
            OpenAIModel = NullIfEmpty(OpenAIModelBox.Text.Trim()),
            CheckForUpdatesOnStartup = CheckUpdatesBox.IsChecked == true,
            BlobConnectionString = KeepOrReplace(_original.BlobConnectionString, BlobBox.Password),
            CosmosPrimaryKey = KeepOrReplace(_original.CosmosPrimaryKey, CosmosKeyBox.Password),
            OpenAIApiKey = KeepOrReplace(_original.OpenAIApiKey, OpenAIKeyBox.Password),
            AcceptedDisclaimerVersion = _original.AcceptedDisclaimerVersion,
        };
        DialogResult = true;
    }

    private static string? KeepOrReplace(string? current, string entered) =>
        string.IsNullOrWhiteSpace(entered) ? current : entered.Trim();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
