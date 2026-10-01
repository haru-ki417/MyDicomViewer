using FellowOakDicom;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Newtonsoft.Json.Linq;

namespace MyDicomViewer.Core.Ai;

/// <summary>AI スクリーニングの結果（確率・判定閾値・Grad-CAM）</summary>
public sealed record PneumoniaResult(float Probability, float Threshold, float[] Cam, int CamSize, TimeSpan Elapsed, string ModelName = "unknown")
{
    public bool IsPositive => Probability >= Threshold;
}

/// <summary>学習時に出力した model_meta.json の内容</summary>
public sealed record ModelMetadata(float Threshold, string ModelName)
{
    public const float DefaultThreshold = 0.5f;

    public static ModelMetadata Load(string path)
    {
        if (!File.Exists(path)) return new ModelMetadata(DefaultThreshold, "unknown");
        var json = JObject.Parse(File.ReadAllText(path));
        return new ModelMetadata(
            json["threshold"]?.Value<float>() ?? DefaultThreshold,
            json["model"]?.Value<string>() ?? "unknown");
    }
}

/// <summary>モデルファイルが置かれていないときの例外</summary>
public sealed class ModelNotFoundException(string path)
    : FileNotFoundException($"AI モデルが見つかりません: {path}", path);

public interface IPneumoniaScreeningService
{
    string ModelDirectory { get; }
    bool IsModelAvailable { get; }
    Task<PneumoniaResult> AnalyzeAsync(DicomDataset dataset, CancellationToken cancellationToken = default);
}

/// <summary>
/// 胸部X線の肺炎所見をローカルの ONNX モデルで推定する。
/// モデルは初回の解析時に読み込み、以降は使い回す（読み込みに数百ミリ秒かかるため）。
/// </summary>
public sealed class PneumoniaScreeningService : IPneumoniaScreeningService, IDisposable
{
    public const string ModelFileName = "pneumonia.onnx";
    public const string MetadataFileName = "model_meta.json";

    private readonly ILogger<PneumoniaScreeningService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private InferenceSession? _session;
    private ModelMetadata? _metadata;

    public PneumoniaScreeningService(string modelDirectory, ILogger<PneumoniaScreeningService> logger)
    {
        ModelDirectory = modelDirectory;
        _logger = logger;
    }

    public string ModelDirectory { get; }
    public bool IsModelAvailable => File.Exists(Path.Combine(ModelDirectory, ModelFileName));

    public async Task<PneumoniaResult> AnalyzeAsync(DicomDataset dataset, CancellationToken cancellationToken = default)
    {
        var (session, metadata) = await EnsureLoadedAsync(cancellationToken);

        return await Task.Run(() =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            byte[] gray = PneumoniaPreprocessor.ToGrayscale(dataset);
            float[] input = PneumoniaPreprocessor.ToInputTensor(gray);
            cancellationToken.ThrowIfCancellationRequested();

            int size = PneumoniaPreprocessor.InputSize;
            using var inputValue = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 3, size, size });
            using var runOptions = new RunOptions();
            using var outputs = session.Run(runOptions, new[] { "input" }, new[] { inputValue }, new[] { "prob", "cam" });

            float probability = outputs[0].GetTensorDataAsSpan<float>()[0];
            float[] cam = outputs[1].GetTensorDataAsSpan<float>().ToArray();
            stopwatch.Stop();

            // 患者を特定できる情報はログに出さない
            _logger.LogInformation("AI screening finished: probability={Probability:F3}, threshold={Threshold:F3}, elapsed={Elapsed} ms",
                probability, metadata.Threshold, stopwatch.ElapsedMilliseconds);
            return new PneumoniaResult(probability, metadata.Threshold, cam, size, stopwatch.Elapsed, metadata.ModelName);
        }, cancellationToken);
    }

    private async Task<(InferenceSession, ModelMetadata)> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_session is not null && _metadata is not null) return (_session, _metadata);

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is null || _metadata is null)
            {
                string modelPath = Path.Combine(ModelDirectory, ModelFileName);
                if (!File.Exists(modelPath)) throw new ModelNotFoundException(modelPath);

                _logger.LogInformation("Loading ONNX model from {Path}", modelPath);
                _metadata = ModelMetadata.Load(Path.Combine(ModelDirectory, MetadataFileName));
                _session = await Task.Run(() => new InferenceSession(modelPath), cancellationToken);
            }
            return (_session, _metadata);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _loadLock.Dispose();
    }
}
