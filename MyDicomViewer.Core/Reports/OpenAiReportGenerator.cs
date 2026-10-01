using Microsoft.Extensions.Logging;
using MyDicomViewer.Core.Configuration;
using OpenAI.Chat;

namespace MyDicomViewer.Core.Reports;

/// <summary>レポート生成に渡す情報。患者を特定できる情報は含めない。</summary>
public sealed record ReportRequest(byte[] JpegImage, string Modality, string BodyPart);

public interface IReportGenerator
{
    Task<string> GenerateAsync(ReportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>OpenAI のマルチモーダルモデルで読影レポートのドラフトを生成する（デモ用途）</summary>
public sealed class OpenAiReportGenerator : IReportGenerator
{
    private const string SystemPrompt =
        "あなたは放射線科の読影を補助する AI アシスタントです。提供された医療画像とメタデータを解析し、" +
        "異常所見の有無を評価して読影レポートのドラフトを作成してください。" +
        "DICOM のメタデータ（検査部位など）が欠損していたり画像と矛盾している場合は、画像の解剖学的特徴から部位を推定してください。" +
        "本システムはデモ用途であるため、必ず最後に免責事項を添えてください。";

    private readonly AppConfig _config;
    private readonly ILogger<OpenAiReportGenerator> _logger;

    public OpenAiReportGenerator(AppConfig config, ILogger<OpenAiReportGenerator> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(ReportRequest request, CancellationToken cancellationToken = default)
    {
        var client = new ChatClient(_config.OpenAIModel, _config.OpenAIApiKey);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(BuildUserPrompt(request)),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(request.JpegImage), "image/jpeg")),
        };

        _logger.LogInformation("Requesting report from {Model}", _config.OpenAIModel);
        ChatCompletion completion = await client.CompleteChatAsync(messages, cancellationToken: cancellationToken);
        return completion.Content.Count > 0 ? completion.Content[0].Text : "";
    }

    internal static string BuildUserPrompt(ReportRequest request) => $"""
        以下のメタデータと添付された画像に基づき、読影レポートを作成してください。
        - モダリティ: {request.Modality}
        - 検査部位(DICOMタグ情報): {request.BodyPart}

        【出力フォーマット要件】
        ・タイトルは「【マルチモーダルAI 読影アシスト ({request.Modality} / 画像からの推測部位)】」とし、実際に画像から判定した部位を含めてください。
        ・[基本情報]
        ・[AI画像所見] (胸部以外の部位であっても、その部位特有の解剖学的特徴や異常の可能性について具体的に言及してください)
        ・[インプレッション (総合評価)]
        ・※AIによるデモ解析である旨の免責事項
        """;
}
