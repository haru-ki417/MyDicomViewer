using System.Collections.Concurrent;
using FellowOakDicom;

namespace MyDicomViewer.Core.Dicom;

/// <summary>1枚の画像のヘッダー情報（並べ替えとシリーズ分けに使う）</summary>
public sealed record DicomInstanceInfo(
    string Path, string SeriesUid, int? SeriesNumber, string SeriesDescription, string Modality,
    int? InstanceNumber, double? SliceLocation, string PatientId = "", string PatientName = "");

/// <summary>同じ撮影条件で撮られた画像のまとまり（シリーズ）</summary>
public sealed record DicomSeriesInfo(
    string SeriesUid, int? SeriesNumber, string Description, string Modality, IReadOnlyList<string> Files,
    string PatientId = "", string PatientName = "")
{
    /// <summary>患者の表示名（名前が無ければ ID）。複数の患者を読み込んだときの見分けに使う。</summary>
    public string PatientLabel => !string.IsNullOrWhiteSpace(PatientName) ? PatientName : PatientId;

    /// <summary>一覧に表示する名前（説明が無ければその旨）</summary>
    public string Title => string.IsNullOrWhiteSpace(Description) ? "（説明なし）" : Description;

    public string DisplayName
    {
        get
        {
            string number = SeriesNumber is int n ? $"#{n} " : "";
            string description = string.IsNullOrWhiteSpace(Description) ? "(説明なし)" : Description;
            return $"{number}{description} [{Modality}] {Files.Count}枚";
        }
    }
}

public sealed record DicomScanResult(IReadOnlyList<DicomSeriesInfo> Series, IReadOnlyList<string> SkippedFiles)
{
    public int ImageCount => Series.Sum(s => s.Files.Count);
}

/// <summary>
/// フォルダや複数ファイルから DICOM を探し、シリーズごとに分けて撮影順に並べる。
/// ヘッダーだけを読む（画像データは読まない）ので、数千枚でも短時間で終わる。
/// </summary>
public static class DicomSeriesScanner
{
    private static readonly string[] DicomExtensions = { ".dcm", ".dicom", ".dic" };

    /// <summary>ファイルとフォルダが混ざった一覧を、DICOM らしいファイルの一覧に展開する</summary>
    public static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    if (LooksLikeDicom(file)) yield return file;
                }
            }
            else if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>拡張子が .dcm などか、ファイル先頭 128 バイトの後に "DICM" の印があれば DICOM とみなす</summary>
    public static bool LooksLikeDicom(string path)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase)) return false;
        if (DicomExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return true;

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 132) return false;
            Span<byte> buffer = stackalloc byte[132];
            stream.ReadExactly(buffer);
            return buffer[128] == 'D' && buffer[129] == 'I' && buffer[130] == 'C' && buffer[131] == 'M';
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static async Task<DicomScanResult> ScanAsync(
        IEnumerable<string> files, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var instances = new ConcurrentBag<DicomInstanceInfo>();
        var skipped = new ConcurrentBag<string>();
        int done = 0;

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                try
                {
                    var file = await DicomFile.OpenAsync(path, FileReadOption.SkipLargeTags);
                    if (IsImageInstance(file.Dataset)) instances.Add(ReadInfo(path, file.Dataset));
                    else skipped.Add(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // DICOM として読めないファイルは飛ばし、あとで件数を知らせる
                    skipped.Add(path);
                }
                progress?.Report(Interlocked.Increment(ref done));
            });

        return new DicomScanResult(Group(instances), skipped.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>壊れたファイルや DICOMDIR などを除くため、画像の識別子と大きさの情報があるものだけを対象にする</summary>
    private static bool IsImageInstance(DicomDataset ds) =>
        ds.Contains(DicomTag.SOPInstanceUID) && ds.Contains(DicomTag.Rows) && ds.Contains(DicomTag.Columns);

    internal static DicomInstanceInfo ReadInfo(string path, DicomDataset ds) => new(
        path,
        ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, ""),
        TryGetInt(ds, DicomTag.SeriesNumber),
        ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, "").Trim(),
        ds.GetSingleValueOrDefault(DicomTag.Modality, "OT").Trim(),
        TryGetInt(ds, DicomTag.InstanceNumber),
        ds.TryGetSingleValue<double>(DicomTag.SliceLocation, out double location) ? location : null,
        ds.GetSingleValueOrDefault(DicomTag.PatientID, "").Trim(),
        ds.GetSingleValueOrDefault(DicomTag.PatientName, "").Trim());

    /// <summary>
    /// シリーズごとに分け、画像番号 → スライス位置 → ファイル名の順に並べる。
    /// シリーズは患者 → シリーズ番号 → 説明の順に並べる（複数の患者を開いたとき、同じ患者がまとまるように）。
    /// </summary>
    internal static IReadOnlyList<DicomSeriesInfo> Group(IEnumerable<DicomInstanceInfo> instances) =>
        instances
            .GroupBy(i => string.IsNullOrEmpty(i.SeriesUid) ? "(unknown)" : i.SeriesUid)
            .Select(g =>
            {
                var ordered = g
                    .OrderBy(i => i.InstanceNumber ?? int.MaxValue)
                    .ThenBy(i => i.SliceLocation ?? double.MaxValue)
                    .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var first = ordered[0];
                return new DicomSeriesInfo(g.Key, first.SeriesNumber, first.SeriesDescription, first.Modality,
                    ordered.Select(i => i.Path).ToList(), first.PatientId, first.PatientName);
            })
            .OrderBy(s => s.PatientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SeriesNumber ?? int.MaxValue)
            .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int? TryGetInt(DicomDataset ds, DicomTag tag) =>
        ds.TryGetSingleValue<int>(tag, out int value) ? value : null;
}
