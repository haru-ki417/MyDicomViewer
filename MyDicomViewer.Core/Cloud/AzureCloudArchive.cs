using Azure.Storage.Blobs;
using FellowOakDicom;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using MyDicomViewer.Core.Configuration;
using MyDicomViewer.Core.Dicom;
using Newtonsoft.Json;

namespace MyDicomViewer.Core.Cloud;

/// <summary>Cosmos DB に保存するメタデータ（匿名化済みの情報のみ）</summary>
public sealed class DicomRecord
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("patientId")] // Cosmos DB のパーティションキー
    public string? PatientId { get; set; }

    public string? PatientName { get; set; }
    public string? Modality { get; set; }
    public string? BlobUrl { get; set; }
    public DateTimeOffset? UploadedAt { get; set; }
}

public interface ICloudArchive
{
    /// <summary>匿名化してから保存する（匿名化前のデータは外部に送らない）</summary>
    Task<DicomRecord> UploadAsync(DicomDataset dataset, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DicomRecord>> SearchAsync(string? patientIdKeyword, CancellationToken cancellationToken = default);
    Task<string> DownloadAsync(DicomRecord record, string destinationDirectory, CancellationToken cancellationToken = default);
}

/// <summary>Azure Blob Storage（画像）+ Cosmos DB（メタデータ）によるクラウド保管</summary>
public sealed class AzureCloudArchive : ICloudArchive, IDisposable
{
    private readonly AppConfig _config;
    private readonly ILogger<AzureCloudArchive> _logger;
    private readonly Lazy<BlobContainerClient> _blobContainer;
    private readonly Lazy<CosmosClient> _cosmos;
    private bool _containerEnsured;

    public AzureCloudArchive(AppConfig config, ILogger<AzureCloudArchive> logger)
    {
        _config = config;
        _logger = logger;
        // 接続は初回利用時に作り、以降は使い回す（毎回作ると遅く、接続も無駄に増える）
        _blobContainer = new Lazy<BlobContainerClient>(() =>
            new BlobServiceClient(_config.BlobConnectionString).GetBlobContainerClient(_config.BlobContainerName));
        _cosmos = new Lazy<CosmosClient>(() => new CosmosClient(_config.CosmosEndpointUri, _config.CosmosPrimaryKey));
    }

    public async Task<DicomRecord> UploadAsync(DicomDataset dataset, CancellationToken cancellationToken = default)
    {
        var deidentified = DicomDeidentifier.Deidentify(dataset);

        var container = _blobContainer.Value;
        if (!_containerEnsured)
        {
            await container.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.None, cancellationToken: cancellationToken);
            _containerEnsured = true;
        }

        var blob = container.GetBlobClient($"{Guid.NewGuid()}.dcm");
        using (var stream = new MemoryStream())
        {
            await new DicomFile(deidentified.Dataset).SaveAsync(stream);
            stream.Position = 0;
            await blob.UploadAsync(stream, overwrite: true, cancellationToken);
        }

        var record = new DicomRecord
        {
            PatientId = deidentified.PseudonymId,
            PatientName = DicomDeidentifier.AnonymousPatientName,
            Modality = deidentified.Dataset.GetSingleValueOrDefault(DicomTag.Modality, "OT"),
            BlobUrl = blob.Uri.ToString(),
            UploadedAt = DateTimeOffset.UtcNow,
        };
        await RecordsContainer.UpsertItemAsync(record, new PartitionKey(record.PatientId), cancellationToken: cancellationToken);

        _logger.LogInformation("Uploaded de-identified study as {RecordId}", record.Id);
        return record;
    }

    public async Task<IReadOnlyList<DicomRecord>> SearchAsync(string? patientIdKeyword, CancellationToken cancellationToken = default)
    {
        var results = new List<DicomRecord>();
        using var iterator = RecordsContainer.GetItemQueryIterator<DicomRecord>(BuildSearchQuery(patientIdKeyword));
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken))
            {
                results.Add(item);
            }
        }
        return results;
    }

    public async Task<string> DownloadAsync(DicomRecord record, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(record.BlobUrl)) throw new InvalidOperationException("この記録には画像の保存先がありません。");

        // 非公開コンテナーなので、URL から Blob 名を取り出し、認証付きのクライアントで取得する
        string blobName = new BlobUriBuilder(new Uri(record.BlobUrl)).BlobName;
        Directory.CreateDirectory(destinationDirectory);
        string path = Path.Combine(destinationDirectory, $"{record.Id}.dcm");
        await _blobContainer.Value.GetBlobClient(blobName).DownloadToAsync(path, cancellationToken);
        return path;
    }

    /// <summary>入力値はクエリ文字列に埋め込まず、パラメーターとして渡す（インジェクション対策）</summary>
    internal static QueryDefinition BuildSearchQuery(string? patientIdKeyword) =>
        string.IsNullOrWhiteSpace(patientIdKeyword)
            ? new QueryDefinition("SELECT * FROM c")
            : new QueryDefinition("SELECT * FROM c WHERE CONTAINS(c.patientId, @keyword)")
                .WithParameter("@keyword", patientIdKeyword.Trim());

    private Container RecordsContainer => _cosmos.Value.GetContainer(_config.CosmosDatabaseId, _config.CosmosContainerId);

    public void Dispose()
    {
        if (_cosmos.IsValueCreated) _cosmos.Value.Dispose();
    }
}
