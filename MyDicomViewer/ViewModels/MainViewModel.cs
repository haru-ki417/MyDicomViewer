using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Extensions.Logging;
using MyDicomViewer.Core.Ai;
using MyDicomViewer.Core.Cloud;
using MyDicomViewer.Core.Configuration;
using MyDicomViewer.Core.Dicom;
using MyDicomViewer.Core.Imaging;
using MyDicomViewer.Core.Reports;
using MyDicomViewer.Infrastructure;
using MyDicomViewer.Services;

namespace MyDicomViewer.ViewModels;

/// <summary>
/// メイン画面の状態と操作。画面部品には直接触らず、プロパティとコマンドだけを公開する（MVVM）。
/// マウス操作（ズーム・移動・計測）の座標計算は表示側（DicomViewport）が行い、結果だけをここに渡す。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const string NeutralColor = "#A3B1BE";
    private const string PositiveColor = "#FF7A6B";
    private const string NegativeColor = "#6FCF97";
    private const string ScreeningIdleText = "AI 肺炎スクリーニングを実行";
    private const string ReportIdleText = "AI マルチモーダル画像解析を実行";

    private readonly ICloudArchive _cloud;
    private readonly IReportGenerator _reports;
    private readonly IPneumoniaScreeningService _screening;
    private readonly IDialogService _dialogs;
    private readonly IImageRenderer _renderer;
    private readonly IAppShell _shell;
    private readonly ILogger<MainViewModel> _logger;

    private IReadOnlyList<string> _paths = Array.Empty<string>();
    private DicomFile? _currentFile;
    private DicomImage? _currentImage;
    private PixelSpacing? _pixelSpacing;
    private MeasurementItem? _activeMeasurement;
    private PneumoniaResult? _lastResult;
    private DicomFile? _lastResultFile;
    private double _defaultWindowCenter = 40, _defaultWindowWidth = 400;
    private bool _resetWindowOnNextLoad;
    private bool _suppressWindowRender;
    private bool _applyingPreset;

    public MainViewModel(
        ICloudArchive cloud, IReportGenerator reports, IPneumoniaScreeningService screening,
        IDialogService dialogs, IImageRenderer renderer, IAppShell shell, ILogger<MainViewModel> logger)
    {
        _shell = shell;

        // どれかの処理が動いている間は、画面上部に作業中のバーを出す
        foreach (var command in new IAsyncRelayCommand[]
                 {
                     OpenFilesCommand, OpenFolderCommand, UploadCommand, SearchCommand, DownloadCommand,
                     GenerateReportCommand, RunScreeningCommand, SaveAiResultCommand, CheckForUpdatesCommand,
                 })
        {
            command.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) OnPropertyChanged(nameof(IsWorking));
            };
        }
        _cloud = cloud;
        _reports = reports;
        _screening = screening;
        _dialogs = dialogs;
        _renderer = renderer;
        _logger = logger;
    }

    /// <summary>表示位置と拡大率を初期状態に戻すよう、表示側に依頼する</summary>
    public event EventHandler? ResetViewRequested;

    // ================================================================ 表示状態
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPatientIdLine))] private string _patientName = "--";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPatientIdLine))] private string _patientId = "--";
    [ObservableProperty] private string _modality = "--";
    [ObservableProperty] private string _studyDate = "";
    [ObservableProperty] private string _sliceText = "0 / 0";
    [ObservableProperty] private int _sliceIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMultipleSlices))] private int _sliceMaximum;

    public bool HasMultipleSlices => SliceMaximum > 0;
    [ObservableProperty] private double _windowCenter = 40;
    [ObservableProperty] private double _windowWidth = 400;
    [ObservableProperty] private double _windowCenterMinimum = -1000;
    [ObservableProperty] private double _windowCenterMaximum = 3000;
    [ObservableProperty] private double _windowWidthMaximum = 4000;
    [ObservableProperty] private ImageSource? _displayImage;
    [ObservableProperty] private int _imageWidth;
    [ObservableProperty] private int _imageHeight;
    [ObservableProperty] private string _statusMessage = "DICOM ファイルまたはフォルダを開いてください（ウィンドウへのドラッグ＆ドロップも可）";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWorking))] private bool _isBusy;

    /// <summary>読み込み・AI 解析・通信などの処理中か</summary>
    public bool IsWorking =>
        IsBusy || OpenFilesCommand.IsRunning || OpenFolderCommand.IsRunning || UploadCommand.IsRunning ||
        SearchCommand.IsRunning || DownloadCommand.IsRunning || GenerateReportCommand.IsRunning ||
        RunScreeningCommand.IsRunning || SaveAiResultCommand.IsRunning || CheckForUpdatesCommand.IsRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand), nameof(GenerateReportCommand), nameof(RunScreeningCommand), nameof(ShowTagsCommand))]
    private bool _hasImage;

    // ---- 画像の四隅に重ねる情報（一般的な DICOM ビューアと同じ配置）
    [ObservableProperty] private string _overlayTopLeft = "";
    [ObservableProperty] private string _overlayTopRight = "";
    [ObservableProperty] private string _overlayBottomLeft = "";
    [ObservableProperty] private string _overlayBottomRight = "";
    [ObservableProperty] private double _zoomPercent = 100;

    // ---- シリーズ
    public ObservableCollection<DicomSeriesInfo> Series { get; } = new();

    /// <summary>複数の患者を読み込んだか（シリーズ一覧に患者を表示して見分けられるようにする）</summary>
    [ObservableProperty] private bool _hasMultiplePatients;

    /// <summary>患者 ID が名前と同じ（匿名化データなど）なら、ID の行は出さない</summary>
    public bool ShowPatientIdLine => !string.Equals(PatientId, PatientName, StringComparison.Ordinal);
    [ObservableProperty] private DicomSeriesInfo? _selectedSeries;

    // ---- ウィンドウのプリセット
    public IReadOnlyList<WindowPreset> WindowPresets => WindowPreset.All;
    [ObservableProperty] private WindowPreset? _selectedPreset;

    // ---- 計測
    public ObservableCollection<MeasurementItem> Measurements { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPanTool), nameof(IsMeasureTool))] private ViewerTool _activeTool = ViewerTool.Pan;
    [ObservableProperty] private string _pixelSpacingText = "";
    [ObservableProperty] private double _overlayStrokeThickness = 2;
    [ObservableProperty] private double _overlayFontSize = 14;

    public bool IsPanTool
    {
        get => ActiveTool == ViewerTool.Pan;
        set { if (value) ActiveTool = ViewerTool.Pan; }
    }

    public bool IsMeasureTool
    {
        get => ActiveTool == ViewerTool.Measure;
        set => ActiveTool = value ? ViewerTool.Measure : ViewerTool.Pan;
    }

    // ---- AI スクリーニング
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsHeatmapVisible))] private ImageSource? _heatmapImage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsHeatmapVisible), nameof(HeatmapNote), nameof(IsNegativeHeatmapShown))]
    private bool _showHeatmap = true;
    [ObservableProperty] private double _heatmapOpacity = 0.6;
    [ObservableProperty] private string _screeningProbability = "--";
    [ObservableProperty] private string _screeningVerdict = "未実行";
    [ObservableProperty] private string _screeningVerdictColor = NeutralColor;
    [ObservableProperty] private string _screeningDetail = "";
    [ObservableProperty] private string _screeningButtonText = ScreeningIdleText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HeatmapNote), nameof(IsNegativeHeatmapShown))] private bool _hasScreeningResult;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HeatmapNote), nameof(IsNegativeHeatmapShown))] private bool _isResultPositive;

    /// <summary>陰性なのに利用者がヒートマップを表示している（注意を促す）</summary>
    public bool IsNegativeHeatmapShown => HasScreeningResult && !IsResultPositive && ShowHeatmap;

    /// <summary>
    /// ヒートマップの説明。Grad-CAM は「その画像の中で相対的に強く反応した場所」を赤くするため、
    /// 陰性の画像でもどこかが必ず赤くなる。陰性のときは、その赤が所見の位置ではないことをはっきり書く。
    /// </summary>
    public string HeatmapNote => !HasScreeningResult
        ? "AI 解析を実行すると表示されます。"
        : IsResultPositive
            ? "赤い領域ほど、AI の判断への寄与が大きい箇所です。"
            : ShowHeatmap
                ? "陰性の画像です。赤い部分は所見の位置ではなく、反応がほぼ無い中で相対的に強かった場所にすぎません。"
                : "陰性のため表示していません。陰性の画像でも、反応がほぼ無い中で最も強かった場所が赤く表示され、所見の位置と誤解されるおそれがあるためです。";
    [ObservableProperty] private double _probabilityFraction;
    [ObservableProperty] private double _thresholdFraction;
    [ObservableProperty] private string _thresholdText = "";

    public bool IsHeatmapVisible => HeatmapImage is not null && ShowHeatmap;

    // ---- レポート
    [ObservableProperty] private string _reportText = "";
    [ObservableProperty] private string _reportButtonText = ReportIdleText;

    // ---- クラウド
    [ObservableProperty] private string _searchKeyword = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DownloadCommand))] private DicomRecord? _selectedRecord;
    public ObservableCollection<DicomRecord> SearchResults { get; } = new();

    // ================================================================ ファイル・フォルダの読み込み
    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        var files = _dialogs.PickDicomFiles();
        if (files.Count > 0) await OpenPathsAsync(files);
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        string? folder = _dialogs.PickFolder();
        if (folder is not null) await OpenPathsAsync(new[] { folder });
    }

    /// <summary>ファイル・フォルダ（ドラッグ＆ドロップ含む）を読み込み、シリーズごとに分けて表示する</summary>
    public async Task OpenPathsAsync(IEnumerable<string> inputs)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusMessage = "DICOM ファイルを探しています...";
            var inputList = inputs.ToList();
            var files = await Task.Run(() => DicomSeriesScanner.ExpandPaths(inputList).ToList());
            if (files.Count == 0)
            {
                StatusMessage = "DICOM ファイルが見つかりませんでした";
                _dialogs.ShowInfo("DICOM ファイルが見つかりませんでした。");
                return;
            }

            var progress = new Progress<int>(n => StatusMessage = $"ヘッダーを読み込み中... {n} / {files.Count}");
            var result = await DicomSeriesScanner.ScanAsync(files, progress);

            Series.Clear();
            foreach (var series in result.Series) Series.Add(series);
            HasMultiplePatients = result.Series.Select(x => x.PatientId).Distinct(StringComparer.Ordinal).Skip(1).Any();

            if (result.Series.Count == 0)
            {
                StatusMessage = "読み込める DICOM 画像がありませんでした";
                _dialogs.ShowInfo("選択したファイルは DICOM として読み込めませんでした。");
                return;
            }

            SelectedSeries = null; // 同じシリーズを選び直しても読み込まれるように一度外す
            SelectedSeries = result.Series[0];

            string skipped = result.SkippedFiles.Count > 0 ? $"（読み込めないファイル {result.SkippedFiles.Count} 件をスキップ）" : "";
            StatusMessage = $"{result.ImageCount} 枚 / {result.Series.Count} シリーズを読み込みました{skipped}";
            _logger.LogInformation("Opened {Images} images in {Series} series ({Skipped} skipped)",
                result.ImageCount, result.Series.Count, result.SkippedFiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open DICOM files");
            _dialogs.ShowError($"読み込み中にエラーが発生しました:\n{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedSeriesChanged(DicomSeriesInfo? value)
    {
        if (value is null) return;

        _paths = value.Files;
        _resetWindowOnNextLoad = true;
        SliceMaximum = Math.Max(0, _paths.Count - 1);
        if (SliceIndex == 0) LoadSlice(0);
        else SliceIndex = 0; // OnSliceIndexChanged から LoadSlice が呼ばれる
        ResetViewRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSliceIndexChanged(int value) => LoadSlice(value);

    [RelayCommand]
    private void NextSlice() => StepSlice(+1);

    [RelayCommand]
    private void PreviousSlice() => StepSlice(-1);

    /// <summary>スライスを前後に移動する（マウスホイール・キーボード用）</summary>
    public void StepSlice(int delta)
    {
        if (_paths.Count <= 1) return;
        SliceIndex = Math.Clamp(SliceIndex + delta, 0, _paths.Count - 1);
    }

    public int SliceCount => _paths.Count;

    private void LoadSlice(int index)
    {
        if (index < 0 || index >= _paths.Count) return;

        try
        {
            var file = DicomFile.Open(_paths[index]);
            var dataset = file.Dataset;
            var image = new DicomImage(dataset);

            _currentFile = file;
            _currentImage = image;
            _pixelSpacing = PixelSpacing.TryRead(dataset);

            PatientName = ReadText(dataset, DicomTag.PatientName);
            PatientId = ReadText(dataset, DicomTag.PatientID);
            Modality = ReadText(dataset, DicomTag.Modality);
            SliceText = $"{index + 1} / {_paths.Count}";
            ImageWidth = image.Width;
            ImageHeight = image.Height;
            OverlayStrokeThickness = Math.Max(1.5, Math.Max(image.Width, image.Height) / 400.0);
            OverlayFontSize = Math.Max(12, Math.Max(image.Width, image.Height) / 40.0);
            PixelSpacingText = _pixelSpacing switch
            {
                null => "画素間隔の情報なし（計測は画素数で表示）",
                { Source: PixelSpacingSource.ImagerPixelSpacing } s => $"画素間隔 {s.Row:0.###} x {s.Column:0.###} mm（検出器面の値。* は拡大率の補正前）",
                var s => $"画素間隔 {s.Row:0.###} x {s.Column:0.###} mm",
            };

            _defaultWindowCenter = image.WindowCenter;
            _defaultWindowWidth = Math.Max(1, image.WindowWidth);
            UpdateWindowRange(dataset);

            _suppressWindowRender = true;
            if (_resetWindowOnNextLoad)
            {
                // 新しく開いたときは、画像に記録された推奨のウィンドウ値を使う
                WindowCenter = _defaultWindowCenter;
                WindowWidth = _defaultWindowWidth;
                SelectedPreset = WindowPreset.ImageDefault;
                _resetWindowOnNextLoad = false;
            }
            image.WindowCenter = WindowCenter;
            image.WindowWidth = WindowWidth;
            _suppressWindowRender = false;

            DisplayImage = _renderer.Render(image);
            Measurements.Clear();
            ClearScreeningResult();
            HasImage = true;
            UpdateOverlays(dataset, index);
            StatusMessage = Path.GetFileName(_paths[index]);
        }
        catch (Exception ex)
        {
            _suppressWindowRender = false;
            _logger.LogWarning(ex, "Failed to load slice {Index}", index);
            _dialogs.ShowError($"スライスの読み込みに失敗しました:\n{ex.Message}");
        }
    }

    /// <summary>スライダーの範囲を、画像が取り得る値の範囲に合わせる</summary>
    private void UpdateWindowRange(DicomDataset dataset)
    {
        int bits = dataset.GetSingleValueOrDefault(DicomTag.BitsStored, (ushort)16);
        bool signed = dataset.GetSingleValueOrDefault(DicomTag.PixelRepresentation, (ushort)0) == 1;
        double slope = dataset.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
        double intercept = dataset.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);

        double storedMin = signed ? -Math.Pow(2, bits - 1) : 0;
        double storedMax = signed ? Math.Pow(2, bits - 1) - 1 : Math.Pow(2, bits) - 1;
        double a = storedMin * slope + intercept, b = storedMax * slope + intercept;
        double min = Math.Min(a, b), max = Math.Max(a, b);

        WindowCenterMinimum = Math.Min(min, _defaultWindowCenter);
        WindowCenterMaximum = Math.Max(max, _defaultWindowCenter);
        WindowWidthMaximum = Math.Max(Math.Max(1, max - min), _defaultWindowWidth);
    }

    // ================================================================ ウィンドウ（濃度）調整
    partial void OnWindowCenterChanged(double value) => ApplyWindow();
    partial void OnWindowWidthChanged(double value) => ApplyWindow();

    private void ApplyWindow()
    {
        if (_suppressWindowRender || _currentImage is null) return;
        _currentImage.WindowCenter = WindowCenter;
        _currentImage.WindowWidth = Math.Max(1, WindowWidth);
        DisplayImage = _renderer.Render(_currentImage);
        UpdateBottomLeftOverlay();

        // 手動で調整したらプリセットの選択を外す（同じプリセットを選び直せるように）
        if (!_applyingPreset && SelectedPreset is not null) SelectedPreset = null;
    }

    partial void OnSelectedPresetChanged(WindowPreset? value)
    {
        if (value is null || _currentImage is null) return;
        _applyingPreset = true;
        try
        {
            WindowCenter = value.IsImageDefault ? _defaultWindowCenter : value.Center;
            WindowWidth = value.IsImageDefault ? _defaultWindowWidth : value.Width;
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    /// <summary>右ドラッグでのウィンドウ調整（横: 幅 / 縦: 中心）。移動量は画面上の画素数。</summary>
    public void AdjustWindow(double deltaX, double deltaY)
    {
        if (_currentImage is null) return;
        double step = Math.Max(1, WindowWidth / 300);
        WindowWidth = Math.Clamp(WindowWidth + deltaX * step, 1, WindowWidthMaximum);
        WindowCenter = Math.Clamp(WindowCenter + deltaY * step, WindowCenterMinimum, WindowCenterMaximum);
    }

    // ================================================================ 表示（ズーム・計測・タグ）
    partial void OnZoomPercentChanged(double value) => UpdateBottomLeftOverlay();

    [RelayCommand]
    private void ResetView() => ResetViewRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ClearMeasurements() => Measurements.Clear();

    /// <summary>計測の開始（座標は画像の画素単位）</summary>
    public void BeginMeasurement(double x, double y)
    {
        if (!HasImage) return;
        (x, y) = ClampToImage(x, y);
        _activeMeasurement = new MeasurementItem { X1 = x, Y1 = y, X2 = x, Y2 = y };
        UpdateMeasurementLabel(_activeMeasurement);
        Measurements.Add(_activeMeasurement);
    }

    public void UpdateMeasurement(double x, double y)
    {
        if (_activeMeasurement is null) return;
        (x, y) = ClampToImage(x, y);
        _activeMeasurement.X2 = x;
        _activeMeasurement.Y2 = y;
        UpdateMeasurementLabel(_activeMeasurement);
    }

    public void EndMeasurement()
    {
        // クリックしただけ（ほとんど動かしていない）の線は残さない
        if (_activeMeasurement is { LengthInPixels: < 2 }) Measurements.Remove(_activeMeasurement);
        _activeMeasurement = null;
    }

    private void UpdateMeasurementLabel(MeasurementItem m)
    {
        m.Label = _pixelSpacing is null
            ? $"{m.LengthInPixels:F0} px"
            : $"{_pixelSpacing.Distance(m.X1, m.Y1, m.X2, m.Y2):F1} mm" +
              (_pixelSpacing.Source == PixelSpacingSource.ImagerPixelSpacing ? " *" : "");
    }

    private (double, double) ClampToImage(double x, double y) =>
        (Math.Clamp(x, 0, ImageWidth), Math.Clamp(y, 0, ImageHeight));

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void ShowTags()
    {
        if (_currentFile is null) return;
        var entries = DicomTagLister.List(_currentFile.FileMetaInfo).Concat(DicomTagLister.List(_currentFile.Dataset)).ToList();
        _dialogs.ShowTags($"DICOM タグ一覧 — {Path.GetFileName(_paths[SliceIndex])}", entries);
    }

    private void UpdateOverlays(DicomDataset dataset, int index)
    {
        string studyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, "");
        if (studyDate.Length == 8) studyDate = $"{studyDate[..4]}/{studyDate[4..6]}/{studyDate[6..]}";

        StudyDate = studyDate;
        OverlayTopLeft = ShowPatientIdLine ? $"{PatientName}\n{PatientId}" : PatientName;
        OverlayTopRight = $"{Modality}  {studyDate}\n{ReadText(dataset, DicomTag.SeriesDescription, "")}";
        OverlayBottomRight = $"Im: {index + 1} / {_paths.Count}\n{ImageWidth} x {ImageHeight}";
        UpdateBottomLeftOverlay();
    }

    private void UpdateBottomLeftOverlay()
    {
        if (!HasImage) return;
        OverlayBottomLeft = $"L: {WindowCenter:F0}  W: {WindowWidth:F0}\nZoom: {ZoomPercent:F0}%";
    }

    // ================================================================ クラウド連携
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task UploadAsync()
    {
        if (_currentFile is null) return;
        await RunSafelyAsync("クラウドへの保存", async () =>
        {
            StatusMessage = "匿名化してアップロード中...";
            await _cloud.UploadAsync(_currentFile.Dataset);
            StatusMessage = "アップロードが完了しました";
            _dialogs.ShowInfo("個人情報を匿名化した上で、Azure への保存が完了しました。");
        });
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        await RunSafelyAsync("検索", async () =>
        {
            var results = await _cloud.SearchAsync(SearchKeyword);
            SearchResults.Clear();
            foreach (var record in results) SearchResults.Add(record);
            StatusMessage = $"{results.Count} 件見つかりました";
        });
    }

    private bool CanDownload() => SelectedRecord is not null;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (SelectedRecord is null) return;
        var record = SelectedRecord;
        await RunSafelyAsync("ダウンロード", async () =>
        {
            string path = await _cloud.DownloadAsync(record, AppPaths.DownloadDirectory);
            await OpenPathsAsync(new[] { path });
        });
    }

    // ================================================================ 生成 AI によるレポート
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task GenerateReportAsync()
    {
        if (_currentFile is null || DisplayImage is not BitmapSource bitmap) return;

        ReportButtonText = "AI が画像を解析中...";
        ReportText = "";
        try
        {
            await RunSafelyAsync("AI レポート生成", async () =>
            {
                var dataset = _currentFile.Dataset;
                var request = new ReportRequest(
                    _renderer.EncodeJpeg(bitmap),
                    ReadText(dataset, DicomTag.Modality),
                    dataset.GetSingleValueOrDefault(DicomTag.BodyPartExamined, "不明"));
                ReportText = await _reports.GenerateAsync(request);
            });
        }
        finally
        {
            ReportButtonText = ReportIdleText;
        }
    }

    // ================================================================ オンデバイス AI スクリーニング
    [RelayCommand(CanExecute = nameof(HasImage))]
    private async Task RunScreeningAsync()
    {
        if (_currentFile is null || _currentImage is null) return;

        string modality = ReadText(_currentFile.Dataset, DicomTag.Modality, "");
        if (modality is not ("" or "CR" or "DX") &&
            !_dialogs.Confirm(
                $"このモデルは胸部X線（CR/DX）用に学習されています。\n現在の画像のモダリティは {modality} のため、結果は参考になりません。\n\nそれでも実行しますか？",
                "モダリティの確認"))
        {
            return;
        }

        var target = _currentFile;
        int width = _currentImage.Width, height = _currentImage.Height;

        ScreeningButtonText = "AI が解析中...";
        try
        {
            var result = await _screening.AnalyzeAsync(target.Dataset);
            var heatmap = await Task.Run(() => HeatmapRenderer.Render(result.Cam, result.CamSize, width, height));

            // 解析中に別の画像へ切り替えられていたら結果を捨てる
            if (!ReferenceEquals(target, _currentFile)) return;

            HeatmapImage = _renderer.ToBitmap(heatmap);
            _lastResult = result;
            _lastResultFile = target;
            SaveAiResultCommand.NotifyCanExecuteChanged();
            ScreeningProbability = FormatProbability(result.Probability);
            ScreeningVerdict = result.IsPositive ? "肺炎所見の可能性あり" : "肺炎を示唆する所見は弱い";

            // ヒートマップは「その画像の中で相対的に強く反応した場所」を赤くするため、陰性でもどこかが赤くなる。
            // 陰性のときに表示すると「ここに所見がある」と誤解させるので、既定では表示しない。
            IsResultPositive = result.IsPositive;
            ShowHeatmap = result.IsPositive;
            ScreeningVerdictColor = result.IsPositive ? PositiveColor : NegativeColor;
            ProbabilityFraction = result.Probability;
            ThresholdFraction = result.Threshold;
            ThresholdText = $"判定閾値 {result.Threshold:P0}";
            HasScreeningResult = true;
            ScreeningDetail = $"EfficientNet-B0 / ONNX Runtime (CPU) / 処理時間 {result.Elapsed.TotalMilliseconds:F0} ms";
        }
        catch (ModelNotFoundException ex)
        {
            _dialogs.ShowInfo(
                $"AI モデルが見つかりません。\n{ex.FileName}\n\npneumonia.onnx と model_meta.json を Models フォルダに置いてください。",
                "モデル未配置");
        }
        catch (InvalidDicomImageException ex)
        {
            _dialogs.ShowInfo(ex.Message, "AI スクリーニング");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI screening failed");
            _dialogs.ShowError($"AI スクリーニング中にエラーが発生しました:\n{ex.Message}");
        }
        finally
        {
            ScreeningButtonText = ScreeningIdleText;
        }
    }

    private bool CanSaveAiResult() => _lastResult is not null && ReferenceEquals(_lastResultFile, _currentFile);

    /// <summary>元画像にヒートマップを重ねた画像を、同じ検査の新しいシリーズ（DICOM 二次取込画像）として保存する</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAiResult))]
    private async Task SaveAiResultAsync()
    {
        if (_lastResult is null || _currentFile is null) return;

        string? path = _dialogs.PickSavePath($"AI_{Path.GetFileNameWithoutExtension(_paths[SliceIndex])}.dcm");
        if (path is null) return;

        var source = _currentFile.Dataset;
        var result = _lastResult;
        // 画面でヒートマップを消している場合は、保存する画像にも重ねない
        double center = WindowCenter, width = WindowWidth, opacity = ShowHeatmap ? HeatmapOpacity : 0;
        string version = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        await RunSafelyAsync("AI 結果の保存", async () =>
        {
            var dataset = await Task.Run(() =>
            {
                var (rgb, w, h) = AiResultExporter.ComposeOverlay(source, result, center, width, opacity);
                return AiResultExporter.CreateSecondaryCapture(source, rgb, w, h, result, result.ModelName, version);
            });
            await new DicomFile(dataset).SaveAsync(path);
            StatusMessage = $"AI の結果を DICOM として保存しました: {Path.GetFileName(path)}";
        });
    }

    /// <summary>表示上 0.0% になる小さな値は「&lt; 0.1%」とする（0% と断定しないため）</summary>
    internal static string FormatProbability(float probability) =>
        probability < 0.0005f ? "< 0.1%" : probability > 0.9995f ? "> 99.9%" : $"{probability:P1}";

    private void ClearScreeningResult()
    {
        _lastResult = null;
        _lastResultFile = null;
        HasScreeningResult = false;
        SaveAiResultCommand.NotifyCanExecuteChanged();
        HeatmapImage = null;
        ScreeningProbability = "--";
        ScreeningVerdict = "未実行";
        ScreeningVerdictColor = NeutralColor;
        ScreeningDetail = "";
    }

    // ================================================================ メニュー（アプリ全体の操作）
    [RelayCommand] private void OpenSettings() => _shell.OpenSettings();
    [RelayCommand] private void ShowWelcome() => _shell.ShowWelcome();
    [RelayCommand] private void ShowAbout() => _shell.ShowAbout();
    [RelayCommand] private Task CheckForUpdatesAsync() => _shell.CheckForUpdatesAsync(quietWhenUpToDate: false);
    [RelayCommand] private void OpenLogFolder() => _shell.OpenLogFolder();
    [RelayCommand] private void Exit() => _shell.Exit();

    // ================================================================ 共通処理
    /// <summary>例外をまとめて扱い、利用者には分かる言葉で、ログには詳細を残す</summary>
    private async Task RunSafelyAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (MissingConfigurationException ex)
        {
            _dialogs.ShowInfo(ex.Message, "設定が必要です");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} failed", operation);
            StatusMessage = $"{operation}に失敗しました";
            _dialogs.ShowError($"{operation}中にエラーが発生しました:\n{ex.Message}");
        }
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
