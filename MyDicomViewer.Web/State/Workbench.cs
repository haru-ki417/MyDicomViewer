using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Imaging.Render;
using Microsoft.JSInterop;
using MyDicomViewer.Core.Ai;
using MyDicomViewer.Core.Dicom;
using MyDicomViewer.Core.Imaging;

namespace MyDicomViewer.Web.State;

public enum ViewerTool
{
    Pan,
    Measure,
}

public enum SidePanel
{
    Image,
    Ai,
    Tags,
}

/// <summary>シリーズの中の 1 枚（ファイルと、複数フレームのときはフレームの番号）</summary>
public sealed record SliceRef(int Input, int Frame, int? InstanceNumber, double? SliceLocation, string Name);

/// <summary>シリーズ（Windows 版の DicomSeriesInfo と同じ並べ方）</summary>
public sealed record SeriesEntry(string Uid, int? Number, string Description, string Modality, string PatientId, string PatientName, IReadOnlyList<SliceRef> Slices)
{
    public string PatientLabel => !string.IsNullOrWhiteSpace(PatientName) ? PatientName : PatientId;

    public string Title => string.IsNullOrWhiteSpace(Description) ? "（説明なし）" : Description;
}

/// <summary>
/// 画面全体の状態（Windows 版の MainViewModel のブラウザー版）。
/// 開いた DICOM はこのブラウザーの中だけで扱い、どこにも送らない。
/// </summary>
public sealed class Workbench(BrowserIo io)
{
    public const string ScreeningIdleText = "AI 肺炎スクリーニングを実行";

    private readonly List<(string Name, byte[] Bytes)> inputs = [];
    private CancellationTokenSource? loadCts;
    private float[]? values;

    public event Action? Changed;

    public void Notify() => Changed?.Invoke();

    // ---- シリーズ

    public List<SeriesEntry> Series { get; } = [];

    public SeriesEntry? Selected { get; private set; }

    public int SliceIndex { get; private set; }

    public int SliceCount => Selected?.Slices.Count ?? 0;

    public int SkippedFiles { get; private set; }

    public bool HasMultiplePatients => Series.Select(s => s.PatientId).Distinct().Count() > 1;

    // ---- 表示中の画像

    public DicomFile? CurrentFile { get; private set; }

    public bool HasImage => CurrentFile is not null;

    public int ImageWidth { get; private set; }

    public int ImageHeight { get; private set; }

    public bool IsColor { get; private set; }

    public bool Invert { get; private set; }

    /// <summary>カラー画像の画素（R, G, B の順）</summary>
    public byte[]? Rgb { get; private set; }

    /// <summary>画像を替えるたびに増える（表示の部品が描き直しを判断する）</summary>
    public int ImageVersion { get; private set; }

    /// <summary>計測と表示位置を保ったまま描き直すか（濃度だけ変えたとき）</summary>
    public bool KeepView { get; private set; }

    public PixelSpacing? Spacing { get; private set; }

    public string PixelSpacingText => Spacing switch
    {
        null => "画素間隔の情報なし（計測は画素数で表示）",
        { Source: PixelSpacingSource.ImagerPixelSpacing } s => string.Create(CultureInfo.InvariantCulture, $"画素間隔 {s.Row:0.###} x {s.Column:0.###} mm（検出器面の値。* は拡大率の補正前）"),
        var s => string.Create(CultureInfo.InvariantCulture, $"画素間隔 {s.Row:0.###} x {s.Column:0.###} mm"),
    };

    public string PatientName { get; private set; } = "--";

    public string PatientId { get; private set; } = "--";

    public string Modality { get; private set; } = "--";

    public string StudyDate { get; private set; } = "";

    public bool ShowPatientIdLine => PatientId != PatientName;

    public string OverlayTopLeft => !HasImage ? "" : ShowPatientIdLine ? $"{PatientName}\n{PatientId}" : PatientName;

    public string OverlayTopRight { get; private set; } = "";

    public string OverlayBottomLeft => !HasImage ? "" : string.Create(CultureInfo.InvariantCulture, $"L: {WindowCenter:F0}  W: {WindowWidth:F0}\nZoom: {ZoomPercent:F0}%");

    public string OverlayBottomRight => !HasImage ? "" : $"Im: {SliceIndex + 1} / {SliceCount}\n{ImageWidth} x {ImageHeight}";

    public string CurrentName => Selected is null || SliceCount == 0 ? "" : Selected.Slices[SliceIndex].Name;

    // ---- 濃度（ウィンドウ）

    public double WindowCenter { get; private set; } = 40;

    public double WindowWidth { get; private set; } = 400;

    public double DefaultCenter { get; private set; }

    public double DefaultWidth { get; private set; } = 1;

    public double CenterMinimum { get; private set; } = -1000;

    public double CenterMaximum { get; private set; } = 3000;

    public double WidthMaximum { get; private set; } = 4000;

    public WindowPreset? SelectedPreset { get; private set; } = WindowPreset.ImageDefault;

    /// <summary>濃度を変えるたびに増える</summary>
    public int WindowVersion { get; private set; }

    private bool resetWindowOnNextLoad = true;

    public void SetWindow(double center, double width, bool fromPreset = false)
    {
        WindowCenter = Math.Clamp(center, CenterMinimum, CenterMaximum);
        WindowWidth = Math.Clamp(width, 1, WidthMaximum);
        if (!fromPreset) SelectedPreset = null;
        WindowVersion++;
        Notify();
    }

    public void ApplyPreset(WindowPreset preset)
    {
        SelectedPreset = preset;
        if (preset.IsImageDefault) SetWindow(DefaultCenter, DefaultWidth, true);
        else SetWindow(preset.Center, preset.Width, true);
    }

    /// <summary>右ドラッグでのウィンドウ調整（横: 幅 / 縦: 中心）。移動量は画面上の画素数（Windows 版と同じ）</summary>
    public void AdjustWindow(double dx, double dy)
    {
        if (!HasImage || IsColor) return;
        double step = Math.Max(1, WindowWidth / 300);
        SetWindow(WindowCenter + dy * step, WindowWidth + dx * step);
    }

    /// <summary>今の濃度で 8 bit の画素にする（グレースケール）</summary>
    public byte[]? RenderGray() => values is null ? null : ImageMath.ApplyWindow(values, WindowCenter, WindowWidth, Invert);

    // ---- 表示の操作

    public ViewerTool Tool { get; private set; } = ViewerTool.Pan;

    public double ZoomPercent { get; private set; } = 100;

    public int MeasureCount { get; private set; }

    /// <summary>計測を消す・全体表示にする（表示の部品が見て実行する）</summary>
    public int ClearMeasuresVersion { get; private set; }

    public int FitVersion { get; private set; }

    public SidePanel Panel { get; set; } = SidePanel.Image;

    public void SetTool(ViewerTool tool)
    {
        Tool = tool;
        Notify();
    }

    public void ClearMeasures()
    {
        ClearMeasuresVersion++;
        Notify();
    }

    public void Fit()
    {
        FitVersion++;
        Notify();
    }

    public void OnView(double zoomPercent, int measures)
    {
        ZoomPercent = zoomPercent;
        MeasureCount = measures;
        Notify();
    }

    // ---- 状態

    public string Status { get; set; } = "DICOM ファイルまたはフォルダを開いてください（画面へのドラッグ＆ドロップも可）";

    public bool IsBusy { get; private set; }

    public double Progress { get; private set; } = -1;

    public string? Error { get; set; }

    // ---- 読み込み

    public async Task LoadPickedAsync(int count, long totalBytes)
    {
        if (count <= 0)
        {
            Error = "読めるファイルがありませんでした。";
            Notify();
            return;
        }
        var list = new List<(string, byte[])>();
        await RunLoadAsync(async cts =>
        {
            long done = 0;
            for (int i = 0; i < count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                string name = await io.FileNameAsync(i);
                var bytes = await io.ReadFileAsync(i);
                if (bytes is null) continue;
                done += bytes.Length;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) list.AddRange(ExpandZip(name, bytes));
                else list.Add((name, bytes));
                if (i % 8 == 7 || i == count - 1)
                {
                    Progress = totalBytes > 0 ? (double)done / totalBytes : (double)(i + 1) / count;
                    Status = $"ファイルを読んでいます（{i + 1} / {count}）…";
                    Notify();
                }
            }
            await io.ReleaseFilesAsync();
            return list;
        });
    }

    /// <summary>見本の DICOM（CC0 の公開画像から作った胸部 X 線 3 枚）</summary>
    public async Task OpenSamplesAsync()
    {
        string[] names = ["lobar_pneumonia.dcm", "influenza_pa.dcm", "influenza_ap.dcm"];
        await RunLoadAsync(async cts =>
        {
            var list = new List<(string, byte[])>();
            for (int i = 0; i < names.Length; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                Status = $"見本を読んでいます（{i + 1} / {names.Length}）…";
                Progress = (double)i / names.Length;
                Notify();
                if (await io.FetchAsync("samples/" + names[i]) is { } bytes) list.Add((names[i], bytes));
            }
            return list;
        });
    }

    private async Task RunLoadAsync(Func<CancellationTokenSource, Task<List<(string Name, byte[] Bytes)>>> read)
    {
        loadCts?.Cancel();
        var cts = loadCts = new CancellationTokenSource();
        IsBusy = true;
        Error = null;
        Progress = 0;
        Status = "ファイルを読んでいます…";
        Notify();
        try
        {
            var list = await read(cts);
            Progress = -1;
            Status = $"DICOM を調べています（{list.Count} 個）…";
            Notify();
            await Task.Delay(30, cts.Token);
            Scan(list);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Error = "読み込めませんでした: " + ex.Message;
        }
        finally
        {
            if (cts == loadCts)
            {
                IsBusy = false;
                Progress = -1;
                Notify();
            }
        }
    }

    private static List<(string, byte[])> ExpandZip(string zipName, byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var list = new List<(string, byte[])>();
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 || entry.FullName.EndsWith('/') || entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue;
            string ext = Path.GetExtension(entry.Name).ToUpperInvariant();
            if (ext is ".JPG" or ".JPEG" or ".PNG" or ".TXT" or ".XML" or ".HTML" or ".PDF" or ".EXE" or ".DLL" or ".INI" or ".JSON" or ".CSV") continue;
            using var s = entry.Open();
            var data = new byte[entry.Length];
            s.ReadExactly(data);
            list.Add((zipName + "/" + entry.FullName, data));
        }
        return list;
    }

    /// <summary>
    /// ヘッダーを読み、シリーズごとに分ける（Windows 版の DicomSeriesScanner と同じ並べ方:
    /// 画像番号 → スライス位置 → ファイル名。シリーズは患者 → シリーズ番号 → 説明）。複数フレームの画像は 1 フレームを 1 枚とする。
    /// </summary>
    private void Scan(List<(string Name, byte[] Bytes)> list)
    {
        var found = new List<(string Uid, int? Number, string Desc, string Modality, string PatientId, string PatientName, SliceRef Slice)>();
        int skipped = 0;
        int baseIndex = inputs.Count;
        var accepted = new List<(string, byte[])>();
        foreach (var (name, bytes) in list)
        {
            try
            {
                var file = DicomFile.Open(new MemoryStream(bytes, writable: false), FileReadOption.SkipLargeTags);
                var ds = file.Dataset;
                if (!ds.Contains(DicomTag.SOPInstanceUID) || !ds.Contains(DicomTag.Rows) || !ds.Contains(DicomTag.Columns))
                {
                    skipped++;
                    continue;
                }
                int index = baseIndex + accepted.Count;
                accepted.Add((name, bytes));
                int frames = Math.Max(1, ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1));
                int? instance = ds.TryGetSingleValue<int>(DicomTag.InstanceNumber, out int inst) ? inst : null;
                double? location = ds.TryGetSingleValue<double>(DicomTag.SliceLocation, out double loc) ? loc : null;
                for (int f = 0; f < frames; f++)
                {
                    found.Add((ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, ""),
                        ds.TryGetSingleValue<int>(DicomTag.SeriesNumber, out int sn) ? sn : null,
                        ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, "").Trim(),
                        ds.GetSingleValueOrDefault(DicomTag.Modality, "OT").Trim(),
                        ds.GetSingleValueOrDefault(DicomTag.PatientID, "").Trim(),
                        ds.GetSingleValueOrDefault(DicomTag.PatientName, "").Trim(),
                        new SliceRef(index, f, instance, location, frames > 1 ? $"{name} [{f + 1}/{frames}]" : name)));
                }
            }
            catch (Exception ex) when (ex is DicomFileException or DicomDataException or IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                skipped++;
            }
        }
        if (found.Count == 0)
        {
            Error = list.Count == 0 ? "ファイルがありませんでした。" : "DICOM の画像が見つかりませんでした。";
            Status = Error;
            return;
        }

        // 新しく開いたものだけにする（Windows 版と同じく、開き直すと前のものは閉じる）
        inputs.Clear();
        foreach (var a in accepted) inputs.Add(a);
        var reindexed = found.Select(x => x with { Slice = x.Slice with { Input = x.Slice.Input - baseIndex } });

        Series.Clear();
        Series.AddRange(reindexed
            .GroupBy(x => string.IsNullOrEmpty(x.Uid) ? "(unknown)" : x.Uid)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.Slice.InstanceNumber ?? int.MaxValue)
                    .ThenBy(x => x.Slice.SliceLocation ?? double.MaxValue)
                    .ThenBy(x => x.Slice.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Slice.Frame)
                    .ToList();
                var first = ordered[0];
                return new SeriesEntry(g.Key, first.Number, first.Desc, first.Modality, first.PatientId, first.PatientName, ordered.Select(x => x.Slice).ToList());
            })
            .OrderBy(s => s.PatientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Number ?? int.MaxValue)
            .ThenBy(s => s.Description, StringComparer.OrdinalIgnoreCase));
        SkippedFiles = skipped;
        resetWindowOnNextLoad = true;
        SelectSeries(Series[0]);
        int images = Series.Sum(s => s.Slices.Count);
        Status = $"{Series.Count} シリーズ・{images} 枚を読み込みました" + (skipped > 0 ? $"（DICOM の画像でない {skipped} 個のファイルは飛ばしました）" : "");
    }

    public void SelectSeries(SeriesEntry series)
    {
        Selected = series;
        resetWindowOnNextLoad = true;
        LoadSlice(0);
        Notify();
    }

    public void StepSlice(int delta)
    {
        if (SliceCount <= 1) return;
        SetSlice(SliceIndex + delta);
    }

    public void SetSlice(int index)
    {
        if (Selected is null) return;
        index = Math.Clamp(index, 0, SliceCount - 1);
        if (index == SliceIndex && HasImage) return;
        LoadSlice(index);
        Notify();
    }

    private void LoadSlice(int index)
    {
        if (Selected is null || index < 0 || index >= Selected.Slices.Count) return;
        var slice = Selected.Slices[index];
        try
        {
            var (name, bytes) = inputs[slice.Input];
            var file = DicomFile.Open(new MemoryStream(bytes, writable: false), FileReadOption.ReadAll);
            var ds = file.Dataset;
            ReadFrame(ds, slice.Frame);

            CurrentFile = file;
            SliceIndex = index;
            Spacing = PixelSpacing.TryRead(ds);
            PatientName = ReadText(ds, DicomTag.PatientName);
            PatientId = ReadText(ds, DicomTag.PatientID);
            Modality = ReadText(ds, DicomTag.Modality);
            string studyDate = ReadText(ds, DicomTag.StudyDate, "");
            StudyDate = studyDate.Length == 8 ? $"{studyDate[..4]}/{studyDate[4..6]}/{studyDate[6..]}" : studyDate;
            OverlayTopRight = $"{Modality}  {StudyDate}\n{ReadText(ds, DicomTag.SeriesDescription, "")}";
            if (resetWindowOnNextLoad)
            {
                // 新しく開いたときは、画像に記録された推奨のウィンドウ値を使う
                WindowCenter = DefaultCenter;
                WindowWidth = DefaultWidth;
                SelectedPreset = WindowPreset.ImageDefault;
                resetWindowOnNextLoad = false;
            }
            KeepView = false;
            ImageVersion++;
            ClearScreeningResult();
            Status = name;
            Error = null;
        }
        catch (Exception ex) when (ex is DicomFileException or DicomDataException or DicomImagingException or InvalidDataException or NotSupportedException or InvalidOperationException or IOException or ArgumentException or IndexOutOfRangeException)
        {
            Error = $"スライスの読み込みに失敗しました: {ex.Message}";
        }
    }

    /// <summary>
    /// 1 フレームの画素を読む。グレースケールは Rescale Slope / Intercept をかけた値（HU など）にする。
    /// 圧縮された DICOM は、ブラウザーで展開できる形式（RLE など）だけ展開する。
    /// </summary>
    private void ReadFrame(DicomDataset source, int frame)
    {
        DicomDataset ds = source;
        if (source.InternalTransferSyntax.IsEncapsulated)
        {
            try
            {
                ds = new DicomTranscoder(source.InternalTransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian).Transcode(source);
            }
            catch (Exception ex) when (ex is DicomCodecException or InvalidOperationException or NotSupportedException or DicomImagingException)
            {
                throw new InvalidDataException("この画像は圧縮の形式（" + source.InternalTransferSyntax.UID.Name + "）のため、ブラウザー版では展開できません。Windows 版で開いてください。", ex);
            }
        }
        if (!ds.Contains(DicomTag.PixelData)) throw new InvalidDataException("画像データ（Pixel Data）が含まれていません。");
        var pixelData = DicomPixelData.Create(ds);
        var pixels = PixelDataFactory.Create(pixelData, Math.Clamp(frame, 0, Math.Max(0, pixelData.NumberOfFrames - 1)));
        int w = pixels.Width, h = pixels.Height;
        ImageWidth = w;
        ImageHeight = h;

        if (pixels is ColorPixelData24 color)
        {
            IsColor = true;
            Invert = false;
            values = null;
            Rgb = color.Data.ToArray();
            DefaultCenter = 128;
            DefaultWidth = 256;
            CenterMinimum = 0;
            CenterMaximum = 255;
            WidthMaximum = 256;
            return;
        }

        IsColor = false;
        Rgb = null;
        double slope = ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
        double intercept = ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
        if (slope == 0) slope = 1;
        var v = new float[w * h];
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float f = (float)(pixels.GetPixel(x, y) * slope + intercept);
                v[y * w + x] = f;
                if (f < min) min = f;
                if (f > max) max = f;
            }
        }
        values = v;
        Invert = ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "").Trim() == "MONOCHROME1";

        // 画像の推奨の濃度（無ければ画素の最小〜最大）
        if (ds.TryGetValues<double>(DicomTag.WindowCenter, out var centers) && centers.Length > 0
            && ds.TryGetValues<double>(DicomTag.WindowWidth, out var widths) && widths.Length > 0 && widths[0] > 0)
        {
            DefaultCenter = centers[0];
            DefaultWidth = widths[0];
        }
        else
        {
            DefaultCenter = (min + max) / 2.0;
            DefaultWidth = Math.Max(1, max - min);
        }

        // スライダーの範囲を、画像が取り得る値の範囲に合わせる（Windows 版と同じ）
        int bits = ds.GetSingleValueOrDefault(DicomTag.BitsStored, (ushort)16);
        bool signed = ds.GetSingleValueOrDefault(DicomTag.PixelRepresentation, (ushort)0) == 1;
        double storedMin = signed ? -Math.Pow(2, bits - 1) : 0;
        double storedMax = signed ? Math.Pow(2, bits - 1) - 1 : Math.Pow(2, bits) - 1;
        double a = storedMin * slope + intercept, b = storedMax * slope + intercept;
        double lo = Math.Min(a, b), hi = Math.Max(a, b);
        CenterMinimum = Math.Min(lo, DefaultCenter);
        CenterMaximum = Math.Max(hi, DefaultCenter);
        WidthMaximum = Math.Max(Math.Max(1, hi - lo), DefaultWidth);
    }

    // ---- タグ・匿名化

    public IReadOnlyList<DicomTagEntry> Tags()
    {
        if (CurrentFile is null) return [];
        return DicomTagLister.List(CurrentFile.FileMetaInfo).Concat(DicomTagLister.List(CurrentFile.Dataset)).ToList();
    }

    /// <summary>表示中の画像を、PS3.15 の基本プロファイルで匿名化して保存する（Windows 版のクラウド保存の前と同じ処理）</summary>
    public async Task SaveAnonymizedAsync()
    {
        if (CurrentFile is null) return;
        try
        {
            var result = DicomDeidentifier.Deidentify(CurrentFile.Dataset);
            using var ms = new MemoryStream();
            await new DicomFile(result.Dataset).SaveAsync(ms);
            await io.DownloadAsync($"anonymized_{result.PseudonymId}.dcm", "application/dicom", ms.ToArray());
            Status = $"匿名化して保存しました（仮の ID: {result.PseudonymId}）";
        }
        catch (Exception ex) when (ex is DicomDataException or InvalidOperationException or IOException)
        {
            Error = "匿名化できませんでした: " + ex.Message;
        }
        Notify();
    }

    // ---- オンデバイス AI スクリーニング

    public ModelInfo? Model { get; private set; }

    public string? ModelError { get; private set; }

    public bool IsScreening { get; private set; }

    public bool AskModality { get; private set; }

    public PneumoniaResult? Result { get; private set; }

    private DicomFile? resultFile;

    public bool ShowHeatmap { get; private set; }

    public double HeatmapOpacity { get; private set; } = 0.6;

    /// <summary>ヒートマップの画素（R, G, B, A）。表示する画像と同じ縦横比</summary>
    public (byte[] Rgba, int Width, int Height)? Heatmap { get; private set; }

    public int HeatmapVersion { get; private set; }

    public string ScreeningDetail { get; private set; } = "";

    public bool CanSaveAiResult => Result is not null && ReferenceEquals(resultFile, CurrentFile);

    public async Task ProbeModelAsync()
    {
        Model = await io.ProbeModelAsync();
        Notify();
    }

    [JSInvokable]
    public void OnModel(ModelInfo? model, string? error)
    {
        if (model is not null) Model = model;
        ModelError = error;
        Notify();
    }

    public async Task ForgetModelAsync()
    {
        await io.ForgetModelAsync();
        Model = await io.ProbeModelAsync();
        Notify();
    }

    public async Task RunScreeningAsync(bool confirmed = false)
    {
        if (CurrentFile is null || IsScreening) return;
        string modality = ReadText(CurrentFile.Dataset, DicomTag.Modality, "");
        if (!confirmed && modality is not ("" or "CR" or "DX"))
        {
            AskModality = true;
            Notify();
            return;
        }
        AskModality = false;
        var target = CurrentFile;
        int width = ImageWidth, height = ImageHeight;
        IsScreening = true;
        ModelError = null;
        Notify();
        try
        {
            await Task.Delay(20);
            byte[] gray = PneumoniaPreprocessor.ToGrayscale(target.Dataset);
            float[] input = PneumoniaPreprocessor.ToInputTensor(gray);
            int size = PneumoniaPreprocessor.InputSize;
            var bytes = await io.RunModelAsync(MemoryMarshal.AsBytes(input.AsSpan()).ToArray(), size);
            var output = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
            var result = new PneumoniaResult(output[0], output[1], output[3..].ToArray(), size, TimeSpan.FromMilliseconds(output[2]), Model?.Name is { Length: > 0 } n ? n : "unknown");

            // 解析中に別の画像へ切り替えられていたら結果を捨てる
            if (!ReferenceEquals(target, CurrentFile)) return;

            var heat = HeatmapRenderer.Render(result.Cam, result.CamSize, width, height);
            var rgba = new byte[heat.Bgra.Length];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = heat.Bgra[i + 2];
                rgba[i + 1] = heat.Bgra[i + 1];
                rgba[i + 2] = heat.Bgra[i];
                rgba[i + 3] = heat.Bgra[i + 3];
            }
            Heatmap = (rgba, heat.Width, heat.Height);
            HeatmapVersion++;
            Result = result;
            resultFile = target;
            // ヒートマップは「その画像の中で相対的に強く反応した場所」を赤くするため、陰性でもどこかが赤くなる。
            // 陰性のときに表示すると「ここに所見がある」と誤解させるので、既定では表示しない（Windows 版と同じ）。
            ShowHeatmap = result.IsPositive;
            ScreeningDetail = string.Create(CultureInfo.InvariantCulture, $"{ModelLabel(result.ModelName)} / ONNX Runtime Web (WebAssembly) / 処理時間 {result.Elapsed.TotalMilliseconds:F0} ms");
            Model = Model is null ? null : Model with { Pending = false };
        }
        catch (InvalidDicomImageException ex)
        {
            ModelError = ex.Message;
        }
        catch (JSException ex)
        {
            ModelError = "AI を実行できませんでした: " + ex.Message;
        }
        finally
        {
            IsScreening = false;
            Notify();
        }
    }

    public void CancelModalityQuestion()
    {
        AskModality = false;
        Notify();
    }

    private static string ModelLabel(string name) => name.Equals("efficientnet_b0", StringComparison.OrdinalIgnoreCase) ? "EfficientNet-B0" : name;

    public void SetHeatmap(bool show, double opacity)
    {
        ShowHeatmap = show;
        HeatmapOpacity = Math.Clamp(opacity, 0, 1);
        Notify();
    }

    private void ClearScreeningResult()
    {
        Result = null;
        resultFile = null;
        Heatmap = null;
        HeatmapVersion++;
        ShowHeatmap = false;
        ScreeningDetail = "";
        AskModality = false;
    }

    /// <summary>表示上 0.0% になる小さな値は「&lt; 0.1%」とする（0% と断定しないため）</summary>
    public static string FormatProbability(float probability) =>
        probability < 0.0005f ? "< 0.1%" : probability > 0.9995f ? "> 99.9%" : (probability * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>元画像にヒートマップを重ねた画像を、同じ検査の新しいシリーズ（DICOM 二次取込画像）として保存する</summary>
    public async Task SaveAiResultAsync()
    {
        if (Result is null || CurrentFile is null || !CanSaveAiResult) return;
        try
        {
            var source = CurrentFile.Dataset;
            double opacity = ShowHeatmap ? HeatmapOpacity : 0;
            var (rgb, w, h) = AiResultExporter.ComposeOverlay(source, Result, WindowCenter, WindowWidth, opacity);
            var dataset = AiResultExporter.CreateSecondaryCapture(source, rgb, w, h, Result, Result.ModelName, "web-1.0.0");
            using var ms = new MemoryStream();
            await new DicomFile(dataset).SaveAsync(ms);
            string baseName = Path.GetFileNameWithoutExtension(CurrentName.Split('/').Last());
            await io.DownloadAsync($"AI_{baseName}.dcm", "application/dicom", ms.ToArray());
            Status = "AI の結果を DICOM（二次取込画像）として保存しました";
        }
        catch (Exception ex) when (ex is InvalidDicomImageException or DicomDataException or InvalidOperationException or IOException)
        {
            Error = "AI の結果を保存できませんでした: " + ex.Message;
        }
        Notify();
    }

    private static string ReadText(DicomDataset dataset, DicomTag tag, string fallback = "Unknown")
    {
        try
        {
            return dataset.Contains(tag) ? dataset.GetString(tag).Trim() : fallback;
        }
        catch (DicomDataException)
        {
            return fallback;
        }
    }
}
