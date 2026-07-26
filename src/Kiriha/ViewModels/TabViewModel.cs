using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiriha.Models;
using Kiriha.Services;

namespace Kiriha.ViewModels;

/// <summary>並べ替え・列を識別するキーの単一情報源。settings.json にこの名前のまま永続化されるため変更しないこと。
/// XAML の CommandParameter（MainWindow.axaml の並べ替えメニュー等）は文字列リテラルのままなので、
/// 追加時は両方を揃える。</summary>
public static class SortKeys
{
    public const string Name = "Name";
    public const string Modified = "Modified";
    public const string Created = "Created";
    public const string Type = "Type";
    public const string Size = "Size";
}

/// <summary>1 タブ分の状態（現在パス・エントリ一覧・履歴・表示モード・固定状態）を持つ ViewModel。</summary>
public partial class TabViewModel : ObservableObject
{
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly ShellOptions _options;
    private readonly FolderViewSettingsService? _folderViewSettings;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _filterDebounceCts;
    private bool _isDetached;
    private bool _isApplyingFolderViewSettings;
    private bool _suppressSearchFilter;
    private long _searchGeneration;
    private long _navigationGeneration;

    internal bool IsApplyingFolderViewSettings => _isApplyingFolderViewSettings;

    [ObservableProperty]
    private string _title = "PC";

    /// <summary>アドレスバーの編集用テキスト（確定前はナビゲーションに影響しない）。</summary>
    [ObservableProperty]
    private string _pathText = "";

    /// <summary>ステータスバー左側（項目数）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>ステータスバーの選択情報（「3 個の項目を選択 12.5 KB」）。</summary>
    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private FileSystemEntry? _selectedEntry;

    /// <summary>Chrome のタブ固定に相当。固定タブは現在の階層に固定され、終了時に保存される。</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>アドレスバーがパンくず表示ではなくテキスト編集中かどうか。</summary>
    [ObservableProperty]
    private bool _isEditingPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsDetailsView), nameof(IsListView), nameof(IsIconsView), nameof(IconFontSize),
        nameof(ListOrientation), nameof(IsGalleryView),
        nameof(IsViewExtraLarge), nameof(IsViewLarge), nameof(IsViewMedium),
        nameof(IsViewSmall), nameof(IsViewList), nameof(IsViewDetails))]
    private ViewMode _viewMode = ViewMode.Details;

    /// <summary>タブ自身（✕ ボタン / コンテキストメニュー）からの閉じる要求。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>固定タブから別階層へ移動しようとしたとき、新しい通常タブで開く要求。</summary>
    public event Action<string>? PinnedNavigationRequested;

    /// <summary>名前の変更 UI の表示要求（View 側でダイアログを出す）。</summary>
    public event EventHandler<FileSystemEntry>? RenameRequested;

    /// <summary>同一パス再読み込み後に複数選択の復元を View（ListBox 所有側）へ依頼する。
    /// タブは動的に生成・破棄されるため、購読管理が単純な static イベントにしている
    /// （ClipboardFileService.CutStateChanged と同じ方式。購読者は MainWindow 1 つ）。</summary>
    public static event Action<TabViewModel, IReadOnlyList<FileSystemEntry>>? SelectionRestoreRequested;

    private List<FileSystemEntry> _selection = new();
    private readonly HashSet<string> _pendingNewFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DetailColumnViewModel> DetailColumns { get; }

    /// <summary>現在の複数選択（切り取り / コピー / 削除の対象）。</summary>
    public IReadOnlyList<FileSystemEntry> Selection => _selection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsSortByName), nameof(IsSortByModified), nameof(IsSortByCreated), nameof(IsSortByType), nameof(IsSortBySize),
        nameof(NameSortGlyph), nameof(ModifiedSortGlyph), nameof(TypeSortGlyph), nameof(SizeSortGlyph), nameof(CreatedSortGlyph))]
    private string _sortKey = SortKeys.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsSortAscending), nameof(IsSortDescending),
        nameof(NameSortGlyph), nameof(ModifiedSortGlyph), nameof(TypeSortGlyph), nameof(SizeSortGlyph), nameof(CreatedSortGlyph))]
    private bool _sortAscendingFlag = true;

    /// <summary>検索ボックスの内容（現在のフォルダー内をインクリメンタル絞り込み）。</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>詳細表示のカラム幅（ヘッダーの Thumb ドラッグで変更）。</summary>
    [ObservableProperty]
    private double _colNameWidth = 300;

    [ObservableProperty]
    private double _colModifiedWidth = 160;

    [ObservableProperty]
    private double _colTypeWidth = 140;

    [ObservableProperty]
    private double _colSizeWidth = 180;

    [ObservableProperty]
    private double _colCreatedWidth = 170;

    /// <summary>列の表示 / 非表示（ヘッダー右クリックで切替、エクスプローラー互換）。</summary>
    [ObservableProperty]
    private bool _showColModified = true;

    [ObservableProperty]
    private bool _showColType = true;

    [ObservableProperty]
    private bool _showColSize = true;

    [ObservableProperty]
    private bool _showColCreated;

    /// <summary>検索ボックスのプレースホルダー（エクスプローラー同様「○○の検索」）。</summary>
    public string SearchPlaceholder => LocalizationService.Text("Text.Search.Placeholder", Title);

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(SearchPlaceholder));

    /// <summary>ステータスバー右側（空き領域）。</summary>
    [ObservableProperty]
    private string _freeSpaceText = "";

    /// <summary>コンパクトビュー（行の高さを詰める）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight), nameof(ListRowHeight))]
    private bool _isCompactView;

    // 通常時は Windows 11 エクスプローラーの標準間隔相当、コンパクト時は従来の詰めた行高
    public double RowHeight => IsCompactView ? 24 : 36;

    /// <summary>Windows エクスプローラーの一覧表示に合わせた行高。</summary>
    public double ListRowHeight => IsCompactView ? 18 : 22;

    // ===== プレビューペイン =====

    private bool _previewEnabled;
    private CancellationTokenSource? _previewCts;

    /// <summary>プレビュー画像（画像ファイル選択時）。</summary>
    [ObservableProperty]
    private Bitmap? _previewBitmap;

    /// <summary>プレビューテキスト（テキストファイル選択時の先頭部分）。</summary>
    [ObservableProperty]
    private string _previewText = "";

    /// <summary>プレビュー下部のファイル情報。</summary>
    [ObservableProperty]
    private string _previewInfo = "";

    private static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico"];

    // Skia（Bitmap.DecodeToWidth）が対応しない新世代画像は、Explorer と同じく
    // シェルの WIC コーデック経由でサムネイル・プレビューを生成する（コーデック未導入なら従来どおりアイコン表示）。
    private static readonly string[] ShellImageThumbnailExtensions =
        [".jxl", ".avif", ".heic", ".heif"];

    private static readonly string[] RawThumbnailExtensions =
    [
        ".3fr", ".ari", ".arw", ".bay", ".cap", ".cr2", ".cr3", ".crw", ".dcr", ".dcs",
        ".dng", ".drf", ".eip", ".erf", ".fff", ".gpr", ".iiq", ".k25", ".kdc", ".mef",
        ".mos", ".mrw", ".nef", ".nrw", ".orf", ".pef", ".ptx", ".pxn", ".raf", ".raw",
        ".rw2", ".rwl", ".rwz", ".sr2", ".srf", ".srw", ".x3f",
    ];

    private static readonly string[] TextExtensions =
        [".txt", ".md", ".log", ".json", ".xml", ".yaml", ".yml", ".ini", ".cs", ".js", ".ts", ".py", ".html", ".css", ".csv", ".bat", ".ps1"];

    /// <summary>プレビューペインの有効 / 無効（ウィンドウ設定から伝播）。</summary>
    public void SetPreviewEnabled(bool enabled)
    {
        _previewEnabled = enabled;
        if (enabled)
        {
            UpdatePreview();
        }
        else
        {
            ClearPreview();
        }
    }

    partial void OnSelectedEntryChanged(FileSystemEntry? value)
    {
        var extension = value is { IsDirectory: false }
            ? Path.GetExtension(value.Name).ToLowerInvariant()
            : "";
        // 静止画は無劣化回転（保存あり）、動画は表示だけの回転。どちらもボタンは同じ。
        CanRotateSelected = ExifOrientationService.CanRotate(extension)
            || VideoPlaybackSession.IsPlayable(extension);

        if (_previewEnabled || IsGalleryView)
        {
            // 別の画像へ移ったら等倍に戻す（前の画像の拡大位置が残っていると何が写っているか分からなくなる）
            ResetGalleryZoom();
            UpdatePreview();
        }

        if (IsGalleryView)
        {
            UpdateGalleryMetadata();
        }
    }

    /// <summary>ギャラリー表示の左側に出す、選択中画像のメタ情報（Exif 等）。</summary>
    public ObservableCollection<GalleryMetadataItem> GalleryMetadata { get; } = new();

    /// <summary>ギャラリー表示中で、選択中画像のメタ情報が 1 件も取れなかったとき true（「情報なし」表示用）。</summary>
    [ObservableProperty]
    private bool _galleryMetadataEmpty;

    private CancellationTokenSource? _metadataCts;

    /// <summary>選択中画像のメタ情報を読み直してパネルへ反映する（ギャラリー表示時のみ）。</summary>
    private async void UpdateGalleryMetadata()
    {
        _metadataCts?.Cancel();
        var cts = new CancellationTokenSource();
        _metadataCts = cts;

        GalleryMetadata.Clear();
        GalleryMetadataEmpty = false;

        var entry = SelectedEntry;
        if (!IsGalleryView || entry is null || entry.IsDirectory)
        {
            return;
        }

        var path = entry.FullPath;
        List<(string Label, string Value)> items;
        try
        {
            items = await Task.Run(() => ImageMetadataService.Read(path), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.Log($"画像メタ情報の取得に失敗: {ex.Message}", LogLevel.Debug);
            return;
        }

        // 取得中に選択が変わった / ギャラリーを抜けた場合は破棄する
        if (cts.Token.IsCancellationRequested || SelectedEntry != entry || !IsGalleryView)
        {
            return;
        }

        foreach (var (label, value) in items)
        {
            GalleryMetadata.Add(new GalleryMetadataItem(label, value));
        }

        GalleryMetadataEmpty = GalleryMetadata.Count == 0;
    }

    private void ClearGalleryMetadata()
    {
        _metadataCts?.Cancel();
        GalleryMetadata.Clear();
        GalleryMetadataEmpty = false;
    }

    /// <summary>ギャラリー表示で選択を前後に送る（メイン画像上のホイールでの切り替え用）。delta 正=次, 負=前。</summary>
    public void MoveGallerySelection(int delta)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var current = SelectedEntry is { } sel ? _entries.IndexOf(sel) : -1;
        var next = current < 0
            ? (delta > 0 ? 0 : _entries.Count - 1)
            : Math.Clamp(current + delta, 0, _entries.Count - 1);
        SelectedEntry = _entries[next];
    }

    // ===== ギャラリーの拡大縮小 =====
    //
    // 拡大は RenderTransform で行う（レイアウトを動かさないので Stretch="Uniform" の
    // 「領域にぴったり収める」計算をそのまま活かせて、拡大中もフィルムストリップや
    // 各オーバーレイの位置が一切ずれない）。等倍が 1.0 で、そこからの倍率だけを持つ。

    /// <summary>ギャラリー拡大率の下限。</summary>
    public const double GalleryZoomMinimum = 0.2;

    /// <summary>ギャラリー拡大率の上限。</summary>
    public const double GalleryZoomMaximum = 8.0;

    /// <summary>表示領域の中心を原点へ寄せる（回転と拡大をこの原点まわりで掛けるため）。</summary>
    private readonly TranslateTransform _galleryCenter = new();
    private readonly RotateTransform _galleryRotate = new();
    private readonly ScaleTransform _galleryScale = new();
    private readonly TranslateTransform _galleryPan = new();

    /// <summary>メイン画像領域の大きさ（コードビハインドの SizeChanged から流し込む）。
    /// 拡大の基準点を領域の中心へ置くために要る。</summary>
    private Size _galleryViewport;

    /// <summary>ドラッグで動かした量。中心合わせの補正とは別に持ち、拡大率が変わっても保つ。</summary>
    private double _galleryPanX;
    private double _galleryPanY;

    /// <summary>メイン画像へ掛ける拡大・平行移動。RenderTransform はビジュアルツリーの外に居て
    /// DataContext を継承しないため、XAML から ScaleX 等を個別に Binding できない。
    /// ここで組み立てた Transform をまるごと渡す。</summary>
    public Transform GalleryImageTransform { get; }

    /// <summary>ギャラリーの拡大率（1.0 = 表示領域に収まるサイズ）。</summary>
    [ObservableProperty]
    private double _galleryZoom = 1.0;

    partial void OnGalleryZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, GalleryZoomMinimum, GalleryZoomMaximum);
        if (Math.Abs(clamped - value) > double.Epsilon)
        {
            GalleryZoom = clamped;
            return;
        }

        // 縮小側へ戻したら、はみ出しを見るための移動量は意味を失うので戻す。
        if (clamped <= 1.0)
        {
            _galleryPanX = 0;
            _galleryPanY = 0;
        }

        ApplyGalleryTransform();
        OnPropertyChanged(nameof(IsGalleryZoomed));
        OnPropertyChanged(nameof(GalleryZoomText));
    }

    /// <summary>表示中の倍率（コントロールバーに出す文字列）。</summary>
    public string GalleryZoomText => $"{GalleryZoom * 100:0}%";

    /// <summary>メイン画像領域の大きさが変わったときに呼ぶ（拡大の基準点が領域の中心のため）。</summary>
    public void SetGalleryViewport(Size size)
    {
        if (_galleryViewport == size)
        {
            return;
        }

        _galleryViewport = size;
        ApplyGalleryTransform();
    }

    /// <summary>
    /// 回転・拡大・移動をまとめて Transform へ反映する。
    ///
    /// 基準点は表示領域の中心。<c>RenderTransformOrigin</c> に任せると要素の実寸に依存して
    /// 左上方向へ広がることがあるため、XAML 側の原点は左上に固定し、ここで
    /// 「中心へ寄せる → 回す → 拡大する → 中心へ戻す（＋ドラッグ量）」の順に組み立てる。
    /// </summary>
    private void ApplyGalleryTransform()
    {
        var centerX = _galleryViewport.Width / 2;
        var centerY = _galleryViewport.Height / 2;
        var scale = GalleryZoom * RotationFitScale();

        _galleryCenter.X = -centerX;
        _galleryCenter.Y = -centerY;
        _galleryRotate.Angle = GalleryDisplayRotation;
        _galleryScale.ScaleX = scale;
        _galleryScale.ScaleY = scale;
        _galleryPan.X = centerX + _galleryPanX;
        _galleryPan.Y = centerY + _galleryPanY;
    }

    /// <summary>
    /// 90 度・270 度回転したときに、回った映像が表示領域へ収まるよう掛ける補正倍率。
    ///
    /// 回転は表示領域と同じ大きさのパネルごと回すため、補正しないと横長の映像を縦にしたときに
    /// 左右がはみ出す。等倍時に実際に描かれている大きさ（Uniform で収めた結果）から算出する。
    /// </summary>
    private double RotationFitScale()
    {
        if (GalleryDisplayRotation % 180 == 0
            || _galleryViewport.Width <= 0 || _galleryViewport.Height <= 0)
        {
            return 1.0;
        }

        var source = VideoFrame?.PixelSize ?? PreviewBitmap?.PixelSize;
        if (source is not { Width: > 0, Height: > 0 } pixels)
        {
            return 1.0;
        }

        var fit = Math.Min(_galleryViewport.Width / pixels.Width, _galleryViewport.Height / pixels.Height);
        var renderedWidth = pixels.Width * fit;
        var renderedHeight = pixels.Height * fit;
        return Math.Min(_galleryViewport.Width / renderedHeight, _galleryViewport.Height / renderedWidth);
    }

    /// <summary>表示だけの回転角（0 / 90 / 180 / 270）。動画はファイルを書き換えず、ここだけを回す。</summary>
    [ObservableProperty]
    private double _galleryDisplayRotation;

    partial void OnGalleryDisplayRotationChanged(double value) => ApplyGalleryTransform();

    /// <summary>等倍より拡大されている（ドラッグでの移動を受け付ける）。</summary>
    public bool IsGalleryZoomed => GalleryZoom > 1.0;

    /// <summary>ホイールやスライダーからの相対ズーム。</summary>
    public void ZoomGalleryBy(double delta)
        => GalleryZoom = Math.Clamp(GalleryZoom + delta, GalleryZoomMinimum, GalleryZoomMaximum);

    /// <summary>拡大中に画像をドラッグで動かす。</summary>
    public void PanGalleryBy(double deltaX, double deltaY)
    {
        if (!IsGalleryZoomed)
        {
            return;
        }

        _galleryPanX += deltaX;
        _galleryPanY += deltaY;
        ApplyGalleryTransform();
    }

    /// <summary>拡大率と移動量を初期状態（100%）へ戻す。表示だけの回転も戻す。</summary>
    [RelayCommand]
    public void ResetGalleryZoom()
    {
        _galleryPanX = 0;
        _galleryPanY = 0;
        GalleryDisplayRotation = 0;
        GalleryZoom = 1.0;
        ApplyGalleryTransform();
    }

    // ===== ギャラリーの無劣化回転 =====

    /// <summary>選択中のファイルを無劣化で回転できるか（回転ボタンの活性制御）。</summary>
    [ObservableProperty]
    private bool _canRotateSelected;

    /// <summary>回転（＝Exif の書き込み）を諦めるまでの時間。</summary>
    private static readonly TimeSpan RotateTimeout = TimeSpan.FromSeconds(10);

    [RelayCommand]
    private Task RotateSelectedLeft() => RotateSelectedAsync(clockwise: false);

    [RelayCommand]
    private Task RotateSelectedRight() => RotateSelectedAsync(clockwise: true);

    /// <summary>Exif の向きだけを書き換えて即座に保存し、表示とサムネイルを作り直す。
    /// 動画はファイルを触らず、表示の向きだけを回す。</summary>
    private async Task RotateSelectedAsync(bool clockwise)
    {
        if (!CanRotateSelected || SelectedEntry is not { IsDirectory: false } entry)
        {
            return;
        }

        // 動画は無劣化での回転保存ができない（再エンコードになる）ので、見た目だけ回す。
        if (IsVideoPreview)
        {
            GalleryDisplayRotation = (GalleryDisplayRotation + (clockwise ? 90 : 270)) % 360;
            return;
        }

        var path = entry.FullPath;
        bool rotated;
        try
        {
            // 他プロセス（ウイルス対策等）が oplock を握っていると、書き込み用の open は
            // 例外も出さずに待ち続けることがある。待ちきりにするとコマンドが完了扱いにならず
            // ボタンが二度と押せなくなるため、上限を切って失敗として扱う。
            rotated = await Task.Run(() => ExifOrientationService.TryRotate(path, clockwise))
                .WaitAsync(RotateTimeout);
        }
        catch (TimeoutException)
        {
            Logger.Log($"画像の回転がタイムアウトしました（ファイルがロックされている可能性）: {path}", LogLevel.Warning);
            rotated = false;
        }

        if (!rotated)
        {
            StatusText = LocalizationService.Text("Text.Gallery.RotateFailed");
            return;
        }

        // 選択が変わっていたら、いま出ている別の画像を作り直さない。
        if (!ReferenceEquals(SelectedEntry, entry))
        {
            return;
        }

        ResetGalleryZoom();
        entry.DisposeThumbnail();
        UpdatePreview();
        UpdateGalleryMetadata();
        _ = EnsureThumbnailAsync(entry);
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        StopVideo();
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        PreviewText = "";
        PreviewInfo = "";
    }

    // ===== ギャラリーの動画再生 =====
    //
    // Media Foundation の frame-server 再生（Services/VideoPlaybackSession）を、ここが所有する
    // 描画フレーム同期のループで駆動する（StartVideoPump 参照）。フレームは 2 枚のビットマップを
    // 交互に差し替えて Image へ流すので、描画のためにコードビハインドへ降りる必要がない。
    //
    // 音量・ミュート・速度は全タブ共通の設定として静的に持ち回り、settings.json へも保存する
    // （起動のたびに音量や速度が既定へ戻ると使いづらいため）。ループだけはファイル単位の一時的な
    // 指定として扱い、保存しない。

    private static double s_videoVolume = 0.7;
    private static bool s_videoMuted;
    private static bool s_videoLooping;
    private static double s_videoRate = 1.0;

    /// <summary>速度ドロップダウンに並べる倍率。</summary>
    private static readonly double[] VideoRates = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 4.0];

    /// <summary>音量・ミュート・速度が変わったときに呼ばれる。永続化は MainWindowViewModel が担う
    /// （タブは AppSettings を持たないため、保存経路をコールバックで受け取る）。</summary>
    internal static Action<double, bool, double>? VideoPreferencesChanged;

    /// <summary>保存済みの音量・ミュート・速度を起動時に流し込む。</summary>
    internal static void LoadVideoPreferences(double volume, bool muted, double rate)
    {
        s_videoVolume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0.7;
        s_videoMuted = muted;
        s_videoRate = VideoRates.Contains(rate) ? rate : 1.0;
    }

    private static void SaveVideoPreferences()
        => VideoPreferencesChanged?.Invoke(s_videoVolume, s_videoMuted, s_videoRate);

    private VideoPlaybackSession? _video;

    /// <summary>描画フレームごとの取り出しループが回っているか（StartVideoPump 参照）。</summary>
    private bool _videoPumpActive;

    /// <summary>再生位置をスライダーへ書き戻した最後の時刻（Stopwatch のタイムスタンプ）。</summary>
    private long _lastVideoUiSync;

    private bool _suppressSeek;

    /// <summary>ギャラリーで動画を表示中（コントロールバーの表示条件）。</summary>
    [ObservableProperty]
    private bool _isVideoPreview;

    /// <summary>メイン画像に重ねるオーバーレイ（＝ギャラリーを閉じるボタン）を今出しているか。
    /// 常時出しっぱなしだと鑑賞の邪魔になるので、プレビュー領域でマウスを動かしている間だけ
    /// true にして、止まったら自動で引っ込める
    /// （表示の起点と自動非表示のタイマーは MainWindow 側が持つ）。</summary>
    [ObservableProperty]
    private bool _isGalleryOverlayVisible;

    /// <summary>コーデックが無い等で再生できなかった。</summary>
    [ObservableProperty]
    private bool _isVideoUnavailable;

    /// <summary>いま画面に出すフレーム。</summary>
    [ObservableProperty]
    private WriteableBitmap? _videoFrame;

    [ObservableProperty]
    private bool _isVideoPlaying;

    [ObservableProperty]
    private double _videoDurationSeconds;

    [ObservableProperty]
    private double _videoPositionSeconds;

    [ObservableProperty]
    private string _videoPositionText = "0:00";

    [ObservableProperty]
    private string _videoDurationText = "0:00";

    public double VideoVolume
    {
        get => s_videoVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - s_videoVolume) < 0.0001) return;
            s_videoVolume = clamped;
            _video?.SetVolume(clamped);
            OnPropertyChanged();
            SaveVideoPreferences();
        }
    }

    public bool IsVideoMuted
    {
        get => s_videoMuted;
        set
        {
            if (s_videoMuted == value) return;
            s_videoMuted = value;
            _video?.SetMuted(value);
            OnPropertyChanged();
            SaveVideoPreferences();
        }
    }

    public bool IsVideoLooping
    {
        get => s_videoLooping;
        set
        {
            if (s_videoLooping == value) return;
            s_videoLooping = value;
            _video?.SetLoop(value);
            OnPropertyChanged();
        }
    }

    /// <summary>再生速度。ドロップダウン（<see cref="VideoRateOptions"/>）から選ぶ。</summary>
    public double VideoRate
    {
        get => s_videoRate;
        set
        {
            if (Math.Abs(s_videoRate - value) < 0.0001) return;
            s_videoRate = value;
            _video?.SetRate(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VideoRateIndex));
            SaveVideoPreferences();
        }
    }

    /// <summary>速度ドロップダウンの選択肢。ComboBox へ直接流せるよう表示文字列で持つ
    /// （数値のまま流すとコンパイル済みバインディングで項目テンプレートの型指定が要るため）。</summary>
    public IReadOnlyList<string> VideoRateOptions => s_videoRateOptions;

    private static readonly string[] s_videoRateOptions = [.. VideoRates.Select(FormatRate)];

    /// <summary>ドロップダウンで選択中の速度（<see cref="VideoRateOptions"/> の添字）。
    /// ComboBox とは SelectedIndex でつなぐ。SelectedItem（object 型）へ string の
    /// プロパティを双方向で結ぶと選択が書き戻らなかったため。</summary>
    public int VideoRateIndex
    {
        get
        {
            var index = Array.FindIndex(VideoRates, r => Math.Abs(r - VideoRate) < 0.0001);
            return index >= 0 ? index : Array.IndexOf(VideoRates, 1.0);
        }
        set
        {
            if (value >= 0 && value < VideoRates.Length)
            {
                VideoRate = VideoRates[value];
            }
        }
    }

    private static string FormatRate(double rate) => $"{rate:0.##}x";

    /// <summary>再生 / 一時停止の切り替え（Space とボタンの両方から呼ぶ）。</summary>
    [RelayCommand]
    private void ToggleVideoPlayback()
    {
        if (_video is null) return;
        if (_video.IsPlaying)
        {
            _video.Pause();
        }
        else
        {
            // 末尾で止まっているときは頭出ししてから再生する
            if (VideoDurationSeconds > 0 && VideoPositionSeconds >= VideoDurationSeconds - 0.05)
            {
                _video.Seek(0);
            }

            _video.Play();
        }

        IsVideoPlaying = _video.IsPlaying;
    }

    [RelayCommand]
    private void ToggleVideoMute() => IsVideoMuted = !IsVideoMuted;

    [RelayCommand]
    private void ToggleVideoLoop() => IsVideoLooping = !IsVideoLooping;

    /// <summary>現在位置を相対移動する（Shift+← / →）。</summary>
    public void SeekVideoBy(double deltaSeconds)
    {
        if (_video is null) return;
        _video.Seek(_video.Position + deltaSeconds);
        SyncVideoPosition();
    }

    /// <summary>スライダー操作での明示シーク。タイマー由来の更新では走らせない。</summary>
    partial void OnVideoPositionSecondsChanged(double value)
    {
        if (_suppressSeek || _video is null) return;
        _video.Seek(value);
        VideoPositionText = FormatDuration(value);
    }

    private void StartVideo(FileSystemEntry entry)
    {
        // セッション（＝Media Engine）は作り直さず、ソースだけ差し替える。
        // 作り直すと前の engine の解放と競合して映像が出ないことがある（VideoPlaybackSession.Open 参照）。

        var session = _video;
        if (session is not null)
        {
            ResetVideoUiState();
            IsVideoPreview = true;
            if (session.Open(entry.FullPath))
            {
                return;
            }

            // 開き直しに失敗したセッションは畳んで作り直す
            StopVideo();
        }

        session = VideoPlaybackSession.TryCreate(entry.FullPath);
        IsVideoPreview = true;
        if (session is null)
        {
            IsVideoUnavailable = true;
            return;
        }

        _video = session;
        IsVideoUnavailable = false;
        session.Changed += OnVideoSessionChanged;
        session.Ended += OnVideoSessionEnded;
        session.SetVolume(s_videoVolume);
        session.SetMuted(s_videoMuted);
        session.SetLoop(s_videoLooping);

        StartVideoPump();
    }

    private void StopVideo()
    {
        _videoPumpActive = false;

        if (_video is not null)
        {
            _video.Changed -= OnVideoSessionChanged;
            _video.Ended -= OnVideoSessionEnded;
            _video.Dispose();
            _video = null;
        }

        ResetVideoUiState();
        IsVideoPreview = false;
    }

    /// <summary>コントロールバーの表示値を初期状態へ戻す（セッション自体は畳まない）。</summary>
    private void ResetVideoUiState()
    {
        VideoFrame = null;
        IsVideoPlaying = false;
        IsVideoUnavailable = false;
        _videoStarted = false;
        VideoDurationSeconds = 0;
        VideoDurationText = FormatDuration(0);
        _suppressSeek = true;
        VideoPositionSeconds = 0;
        _suppressSeek = false;
        VideoPositionText = FormatDuration(0);
    }

    /// <summary>別のタブへ切り替わるときに呼ぶ。見えていないタブで音が鳴り続けないよう再生を畳む。</summary>
    public void SuspendGalleryVideo() => StopVideo();

    /// <summary>このタブへ戻ってきたときに呼ぶ。ギャラリーで動画を選んだままなら再生し直す。</summary>
    public void ResumeGalleryVideo()
    {
        if (_video is not null || !IsGalleryView || SelectedEntry is not { IsDirectory: false } entry)
        {
            return;
        }

        if (VideoPlaybackSession.IsPlayable(Path.GetExtension(entry.Name).ToLowerInvariant()))
        {
            UpdatePreview();
        }
    }

    /// <summary>
    /// フレームの取り出しをコンポジターの描画フレーム（＝垂直同期）に合わせて回す。
    ///
    /// 以前は 16ms 間隔の <see cref="DispatcherTimer"/> で回していたが、Win32 のディスパッチャーは
    /// タイマーを WM_TIMER で実装しているためシステムのタイマー分解能（約 15.6ms）へ丸められ、
    /// 実測で毎秒 35 回前後（約 28ms 間隔）しか回らなかった。30fps の動画を 28ms 間隔で取りに行くと、
    /// 1 枚を 1 回分だけ出したり 2 回分続けて出したりする（＝28ms と 57ms が混ざる）ため、
    /// 毎秒 30 枚を取れていても表示間隔がばらついてカクついて見える。
    /// 描画フレームへ同期させると毎秒 60 回まで上がり、30fps なら常に 2 フレーム保持で揃う。
    ///
    /// なお <see cref="VideoPlaybackSession.TryRenderNextFrame"/> は色変換と転送で 1 枚あたり
    /// 3ms 前後かかるため、描画パスの都合で毎秒 数フレームは 1 描画分ずれる。ここを詰めるには
    /// 転送を UI スレッド外へ出す必要があり、engine の生成スレッドと apartment の問題を伴う。
    /// </summary>
    private void StartVideoPump()
    {
        if (_videoPumpActive) return;
        _videoPumpActive = true;
        RequestVideoFrame();
    }

    private void RequestVideoFrame()
    {
        // 単一ウィンドウ構成なのでメインウィンドウをそのまま描画フレームの供給元にする
        var topLevel = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (topLevel is null)
        {
            _videoPumpActive = false;
            return;
        }

        topLevel.RequestAnimationFrame(OnVideoAnimationFrame);
    }

    private void OnVideoAnimationFrame(TimeSpan _)
    {
        if (!_videoPumpActive || _video is null)
        {
            _videoPumpActive = false;
            return;
        }

        if (_video.TryRenderNextFrame())
        {
            VideoFrame = _video.CurrentFrame;
        }

        // 再生位置は毎フレーム書き戻すとスライダーの再レイアウトが描画と競合するので間引く
        var now = Stopwatch.GetTimestamp();
        if (now - _lastVideoUiSync >= Stopwatch.Frequency / 10)
        {
            _lastVideoUiSync = now;
            SyncVideoPosition();
            IsVideoPlaying = _video.IsPlaying;
        }

        RequestVideoFrame();
    }

    private void SyncVideoPosition()
    {
        if (_video is null) return;

        // スライダーへ書き戻すだけなので、シーク扱いにしない
        _suppressSeek = true;
        VideoPositionSeconds = _video.Position;
        _suppressSeek = false;
        VideoPositionText = FormatDuration(_video.Position);
    }

    private void OnVideoSessionChanged(object? sender, EventArgs e)
    {
        if (_video is null || !ReferenceEquals(sender, _video)) return;

        if (_video.State == VideoPlaybackState.Failed)
        {
            IsVideoUnavailable = true;
            return;
        }

        if (Math.Abs(VideoDurationSeconds - _video.Duration) > 0.001)
        {
            VideoDurationSeconds = _video.Duration;
            VideoDurationText = FormatDuration(_video.Duration);
        }

        if (_video.State == VideoPlaybackState.Ready && !_videoStarted)
        {
            _videoStarted = true;
            _video.SetRate(s_videoRate);
            _video.Play();
        }

        IsVideoPlaying = _video.IsPlaying;
    }

    private bool _videoStarted;

    private void OnVideoSessionEnded(object? sender, EventArgs e)
    {
        if (_video is null || !ReferenceEquals(sender, _video)) return;
        IsVideoPlaying = _video.IsPlaying;
    }

    /// <summary>秒数を m:ss / h:mm:ss へ整形する。</summary>
    internal static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private async void UpdatePreview()
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        var entry = SelectedEntry;

        // 次も動画なら engine を残してソースだけ差し替える。動画以外へ移るときだけ完全に畳む。
        var nextIsVideo = IsGalleryView
            && entry is { IsDirectory: false }
            && VideoPlaybackSession.IsPlayable(Path.GetExtension(entry.Name).ToLowerInvariant());
        if (!nextIsVideo)
        {
            StopVideo();
        }

        if (entry is null || entry.IsDirectory)
        {
            PreviewBitmap?.Dispose();
            PreviewBitmap = null;
            PreviewText = "";
            if (entry is null)
            {
                PreviewInfo = _selection.Count > 1
                    ? LocalizationService.Text("Text.Status.ItemsSelected", _selection.Count)
                    : "";
            }
            else
            {
                PreviewInfo = $"{entry.Name}\n" + LocalizationService.Text("Text.Type.FileFolder");
                _ = LoadFolderPreviewInfoAsync(entry, cts.Token);
            }

            return;
        }

        PreviewInfo = $"{entry.Name}\n{entry.TypeText}  {entry.SizeText}\n" + LocalizationService.Text("Text.Tooltip.Modified", entry.ModifiedText);
        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();

        // ギャラリー表示中の動画は静止画プレビューではなく再生セッションへ切り替える。
        // 通常のプレビューペインでは従来どおりシェルサムネイル任せにする（常時再生させない）。
        if (IsGalleryView && VideoPlaybackSession.IsPlayable(ext))
        {
            PreviewBitmap?.Dispose();
            PreviewBitmap = null;
            PreviewText = "";
            StartVideo(entry);
            return;
        }

        try
        {
            // ギャラリー表示中は画面いっぱいに出すため高解像度でデコードする
            var decodeWidth = IsGalleryView ? 1920 : 480;
            if (ImageExtensions.Contains(ext) && entry.Size is < 64 * 1024 * 1024)
            {
                var bmp = await Task.Run(
                    () => ImageDecodeService.TryDecodeToWidth(entry.FullPath, decodeWidth, cts.Token), cts.Token);
                if (cts.IsCancellationRequested)
                {
                    bmp?.Dispose();
                    return;
                }

                if (bmp is not null)
                {
                    PreviewBitmap?.Dispose();
                    PreviewBitmap = bmp;
                    PreviewText = "";
                    // 画像は寸法も表示する
                    PreviewInfo = $"{entry.Name}\n{entry.TypeText}  {entry.SizeText}  {bmp.PixelSize.Width}×{bmp.PixelSize.Height}\n" + LocalizationService.Text("Text.Tooltip.Modified", entry.ModifiedText);
                    return;
                }

                // 読み取り自体に失敗した場合（クラウドドライブの瞬断など）は情報表示のみへフォールスルー
            }

            if (ShellImageThumbnailExtensions.Contains(ext))
            {
                var bmp = await Task.Run(
                    () => ShellThumbnailService.TryGetThumbnail(entry.FullPath, IsGalleryView ? 1024 : 480), cts.Token);
                if (!cts.IsCancellationRequested && bmp is not null)
                {
                    PreviewBitmap?.Dispose();
                    PreviewBitmap = bmp;
                    PreviewText = "";
                    PreviewInfo = $"{entry.Name}\n{entry.TypeText}  {entry.SizeText}  {bmp.PixelSize.Width}×{bmp.PixelSize.Height}\n" + LocalizationService.Text("Text.Tooltip.Modified", entry.ModifiedText);
                    return;
                }

                bmp?.Dispose();
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                // コーデック未導入で取得できなければ情報表示のみへフォールスルー
            }

            if (TextExtensions.Contains(ext) && entry.Size is < 512 * 1024)
            {
                var text = await Task.Run(() =>
                {
                    var lines = File.ReadLines(entry.FullPath).Take(300);
                    return string.Join(Environment.NewLine, lines);
                }, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    PreviewBitmap?.Dispose();
                    PreviewBitmap = null;
                    PreviewText = text;
                }

                return;
            }
        }
        catch (Exception ex)
        {
            // プレビュー失敗は情報表示のみにフォールバック（キャンセルは正常系なのでログ不要）
            if (ex is not OperationCanceledException)
            {
                Logger.LogException($"プレビューを生成できませんでした: {entry.FullPath}", ex);
            }
        }

        // このコールが既に新しい選択に置き換わっている（stale）なら、末尾のリセットで
        // 最新プレビューを消さない。Task.Run が実行前キャンセルで例外化して catch に落ちた場合に
        // ここへフォールスルーするため、リセット前に必ず自分が最新かを確認する。
        if (!ReferenceEquals(cts, _previewCts))
        {
            return;
        }

        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        PreviewText = "";
    }

    /// <summary>フォルダー選択時のプレビュー情報（項目数と合計サイズを非同期計算、上限あり）。</summary>
    private async Task LoadFolderPreviewInfoAsync(FileSystemEntry entry, CancellationToken token)
    {
        try
        {
            var (count, size, capped) = await Task.Run(() =>
            {
                var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
                long total = 0;
                var n = 0;
                foreach (var file in new DirectoryInfo(entry.FullPath).EnumerateFiles("*", options))
                {
                    if (token.IsCancellationRequested || n >= 20000)
                    {
                        return (n, total, true);
                    }

                    n++;
                    total += file.Length;
                }

                return (n, total, false);
            }, token);

            if (!token.IsCancellationRequested && SelectedEntry == entry)
            {
                PreviewInfo = $"{entry.Name}\n" + LocalizationService.Text("Text.Type.FileFolder") + "\n"
                + LocalizationService.Text("Text.Preview.FolderSummary", $"{count}{(capped ? "+" : "")}", FileSystemEntry.FormatSize(size))
                + (capped ? LocalizationService.Text("Text.Preview.OrMore") : "");
            }
        }
        catch (Exception ex)
        {
            // 計算失敗時は基本情報のまま（キャンセルは正常系なのでログ不要）
            if (ex is not OperationCanceledException)
            {
                Logger.LogException($"フォルダー情報を計算できませんでした: {entry.FullPath}", ex);
            }
        }
    }

    // ===== フォルダー変更の自動検知（エクスプローラーと同じ自動更新） =====

    private IDisposable? _watcherSubscription;

    private void SetupWatcher(string path)
    {
        _watcherSubscription?.Dispose();
        _watcherSubscription = null;
        if (_isDetached || path == FileSystemService.ComputerPath)
        {
            return;
        }

        _watcherSubscription = DirectoryObservationService.Subscribe(path, OnObservedDirectoryChanged);
    }

    /// <summary>現在パスの一覧列挙を最後に開始した UTC 時刻。多重リフレッシュ抑止の判定に使う。</summary>
    internal DateTime LastListLoadStartUtc { get; private set; }

    /// <summary>
    /// シェル verb / ドラッグ移動後の「時限の保険リフレッシュ」が必要かどうか。
    /// フォルダー監視が生きていれば変更はイベント経由で反映されるため保険は不要。
    /// 監視を開始できなかった場合（PC ビュー・監視失敗）だけ true。
    /// 検索結果の表示中は NavigateTo が検索を打ち切ってしまうため常に false（F5 は別経路）。
    /// </summary>
    internal bool NeedsShellRefreshBackup
        => _watcherSubscription is null && SearchText.Length == 0;

    private void OnObservedDirectoryChanged(DateTime lastEventUtc)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 最後のファイルシステムイベントより後に列挙を開始済みなら、その読み込みが
            // 変更を反映済みなので再読み込みしない（操作直後の明示 Refresh との二重走行防止）。
            if (!_isDetached && SearchText.Length == 0 && LastListLoadStartUtc <= lastEventUtc)
            {
                NavigateTo(CurrentPath, record: false);
            }
        });
    }

    // ===== 再帰検索（検索ボックスで Enter） =====

    private CancellationTokenSource? _searchCts;

    /// <summary>サブフォルダーを含む検索を実行する（最大 1000 件）。</summary>
    public async Task SearchRecursiveAsync()
    {
        var query = SearchText.Trim();
        if (query.Length == 0 || CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        // Enter からの即時実行時、保留中の絞り込みデバウンスが後から発火して
        // 検索結果を上書きしないようにキャンセルしておく（デバウンス経由の呼び出しでは無害）。
        _filterDebounceCts?.Cancel();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _searchCts = cts;
        var generation = Interlocked.Increment(ref _searchGeneration);
        StatusText = LocalizationService.Text("Text.Search.Searching");

        var root = CurrentPath;
        var showExt = _options.ShowExtensions;
        var showHidden = _options.ShowHidden;
        var useMaterialIcons = _options.IconSet == FileIconSet.Material;
        var preferLight = useMaterialIcons && MaterialIconService.IsLightTheme();
        List<FileSystemEntry> results;
        bool truncated;
        try
        {
            (results, truncated) = await Task.Run(() =>
            {
                // 打ち切り発生の判定は「上限到達で break したか」で行う。ちょうど 1000 件ヒットの
                // 完全走査を「打ち切り」と誤表示しないため、件数だけでは判定しない。
                var wasTruncated = false;
                var list = new List<FileSystemEntry>();
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = showHidden ? FileAttributes.System : FileAttributes.Hidden | FileAttributes.System,
                };
                foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
                {
                    if (cts.IsCancellationRequested)
                    {
                        break;
                    }

                    if (list.Count >= 1000)
                    {
                        wasTruncated = true;
                        break;
                    }

                    var name = item.Name;
                    if (!name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var isDir = item is DirectoryInfo;
                    long? size = null;
                    DateTime? modified = null;
                    try
                    {
                        size = item is FileInfo file ? file.Length : null;
                        modified = item.LastWriteTime;
                    }
                    catch
                    {
                        // 情報が取れなくても一覧には出す
                    }

                    list.Add(new FileSystemEntry
                    {
                        Name = name,
                        DisplayName = !isDir && !showExt && Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem ? stem : name,
                        FullPath = item.FullName,
                        IsDirectory = isDir,
                        Size = size,
                        Modified = modified,
                        MaterialIconKey = useMaterialIcons
                            ? MaterialIconService.ResolveIconKey(name, isDir, preferLight)
                            : "",
                    });
                }

                return (list, wasTruncated);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // ルート消失やドライブ切断など。無言のまま「検索中...」表示で止めない（NavigateToAsync と同じ規約）。
            Logger.LogException($"再帰検索に失敗しました: {root}", ex);
            if (!_isDetached && generation == _searchGeneration)
            {
                StatusText = LocalizationService.Text("Text.Search.Failed", ex.Message);
            }

            return;
        }

        if (cts.IsCancellationRequested || _isDetached || generation != _searchGeneration
            || !string.Equals(root, CurrentPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(query, SearchText.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        ReplaceEntries(ApplySort(results));

        StatusText = truncated
            ? LocalizationService.Text("Text.Search.Truncated", results.Count)
            : LocalizationService.Text("Text.Search.Found", results.Count);
    }

    // ===== アイコンビューの画像・動画・PDFサムネイル =====

    private ThumbnailScope? _thumbnailScope;
    // 最大160論理pxを200% DPIでも等倍表示できる解像度。画質と一覧のメモリ使用量を両立する。
    private const int ThumbnailPixelSize = 320;
    // 1段目に出す低解像度。Exif 埋め込みサムネイルや Windows のサムネイルキャッシュがそのまま返る大きさ。
    private const int ThumbnailPreviewPixelSize = 96;
    private const int WindowsIconPixelSize = 256;
    private readonly SemaphoreSlim _windowsIconGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingWindowsIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>サムネイル読み込みの世代。フォルダー移動のたびに作り直して旧フォルダー分を打ち切る。
    /// 同時実行枠と読み込み中セットも世代ごとに分けることで、応答の遅い項目（クラウド同期フォルダー等）が
    /// 移動先フォルダーの読み込みを塞がないようにする。</summary>
    private sealed class ThumbnailScope
    {
        private readonly CancellationTokenSource _cts;

        public ThumbnailScope(CancellationToken lifetime)
            => _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        public CancellationToken Token => _cts.Token;

        public SemaphoreSlim Gate { get; } = new(4, 4);

        public ConcurrentDictionary<string, byte> Loading { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>この世代を打ち切る。linked CTS は _lifetimeCts へコールバック登録が残るため必ず破棄し、
        /// Gate は実行中のタスクが Release するので破棄せず GC に任せる。</summary>
        public void Cancel()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    /// <summary>サムネイル読み込みを新しい世代へ切り替える（旧世代は即座に打ち切る）。</summary>
    private void ResetThumbnailScope()
    {
        var previous = _thumbnailScope;
        _thumbnailScope = new ThumbnailScope(_lifetimeCts.Token);
        previous?.Cancel();
    }

    private void CancelThumbnailScope()
    {
        _thumbnailScope?.Cancel();
        _thumbnailScope = null;
    }

    partial void OnViewModeChanged(ViewMode value)
    {
        // 表示メニューのプリセットをスライダー値に反映
        IconSize = value switch
        {
            ViewMode.ExtraLargeIcons => 96,
            ViewMode.LargeIcons => 56,
            ViewMode.MediumIcons => 32,
            _ => IconSize,
        };

        if (IsIconsView)
        {
            ResetThumbnailScope();
        }
        else
        {
            CancelThumbnailScope();
        }

        HandleGalleryTransition();
        SaveFolderViewSettings();
    }

    partial void OnIconSizeChanged(double value)
    {
        HandleGalleryTransition();
        SaveFolderViewSettings();
    }

    /// <summary>直前のギャラリー状態。遷移（入る / 出る）を 1 回だけ処理するために保持する。</summary>
    private bool _wasGalleryView;

    private void HandleGalleryTransition()
    {
        if (IsGalleryView == _wasGalleryView)
        {
            return;
        }

        _wasGalleryView = IsGalleryView;
        ResetGalleryZoom();
        if (IsGalleryView)
        {
            // 選択がなければ先頭から閲覧を始め、ギャラリー解像度で読み直す
            SelectedEntry ??= Entries.FirstOrDefault();
            UpdatePreview();
            UpdateGalleryMetadata();
        }
        else if (_previewEnabled)
        {
            UpdatePreview(); // プレビューペイン解像度に戻す
            ClearGalleryMetadata();
        }
        else
        {
            ClearPreview();
            ClearGalleryMetadata();
        }
    }

    partial void OnSortKeyChanged(string value)
        => SaveFolderViewSettings();

    partial void OnSortAscendingFlagChanged(bool value)
        => SaveFolderViewSettings();

    public async Task EnsureThumbnailAsync(FileSystemEntry entry)
    {
        // 安価な判定を先に済ませる。このメソッドは EffectiveViewportChanged から
        // 1 項目あたりレイアウト中に何度も呼ばれるため、対象外のときに拡張子判定まで
        // 走らせると（詳細・一覧表示では毎回まるごと無駄になる）無視できない回数になる。
        if (!IsIconsView || _isDetached || entry.IsDirectory || entry.IsThumbnailFinal
            || _thumbnailScope is not { } scope)
        {
            return;
        }

        var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
        var isImage = ImageExtensions.Contains(extension);
        var isPdf = extension == ".pdf";
        // 動画は再生対象と同じ範囲をサムネイル対象にする（定義を 2 箇所に持たない）
        var useShellThumbnail = VideoPlaybackSession.IsPlayable(extension)
            || RawThumbnailExtensions.Contains(extension)
            || ShellImageThumbnailExtensions.Contains(extension);
        if ((!isImage && !isPdf && !useShellThumbnail)
            || (isImage && entry.Size is null or > 32 * 1024 * 1024)
            || !scope.Loading.TryAdd(entry.FullPath, 0))
        {
            return;
        }

        var token = scope.Token;
        try
        {
            // 1段目: ファイルが持つ低解像度サムネイル（Exif 埋め込み / Windows のサムネイルキャッシュ）。
            // 2段目よりずっと速く返るので、まず絵を出して体感速度を上げる。
            // PDF は Shell からサムネイルを取れないため飛ばす。
            if (!isPdf && entry.Thumbnail is null)
            {
                var preview = await LoadThumbnailAsync(
                    scope,
                    token,
                    () => ShellThumbnailService.TryGetThumbnail(entry.FullPath, ThumbnailPreviewPixelSize))
                    .ConfigureAwait(false);
                PostThumbnail(entry, preview, token, isFinal: false);
            }

            // 2段目: 表示解像度。同時実行枠を握り直すため、画面内の各項目へ1段目が行き渡ってから走る。
            var full = await LoadThumbnailAsync(scope, token, () =>
            {
                if (isPdf)
                {
                    return PdfThumbnailService.TryGetThumbnail(entry.FullPath, ThumbnailPixelSize);
                }

                if (useShellThumbnail)
                {
                    return ShellThumbnailService.TryGetThumbnail(entry.FullPath, ThumbnailPixelSize);
                }

                return ImageDecodeService.TryDecodeToWidth(entry.FullPath, ThumbnailPixelSize, token);
            }).ConfigureAwait(false);
            PostThumbnail(entry, full, token, isFinal: true);
        }
        catch (OperationCanceledException) { }
        // 世代交代で linked CTS を破棄した直後に待機へ入った場合。打ち切りと同じ扱いにする。
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Logger.LogException($"サムネイルを読み込めませんでした: {entry.FullPath}", ex);
        }
        finally
        {
            scope.Loading.TryRemove(entry.FullPath, out _);
        }
    }

    /// <summary>同時実行枠を確保してサムネイル生成を1段だけ実行する。
    /// 枠は段ごとに解放し、高解像度読み込みが他項目の低解像度読み込みを追い越さないようにする。</summary>
    private static async Task<Bitmap?> LoadThumbnailAsync(
        ThumbnailScope scope,
        CancellationToken token,
        Func<Bitmap?> load)
    {
        await scope.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return load();
            }, token).ConfigureAwait(false);
        }
        finally
        {
            scope.Gate.Release();
        }
    }

    /// <summary>読み込み済みサムネイルを UI スレッドへ渡す。読み込み自体は ConfigureAwait(false) で
    /// UI スレッドを経由させず、反映だけを入力より低い優先度で流すことで、画像が大量にあるフォルダーでも
    /// ダブルクリックやスクロールがサムネイル処理に待たされないようにする。</summary>
    private void PostThumbnail(FileSystemEntry entry, Bitmap? bitmap, CancellationToken token, bool isFinal)
        => Dispatcher.UIThread.Post(
            () => PublishThumbnail(entry, bitmap, token, isFinal),
            DispatcherPriority.Background);

    /// <summary>読み込んだサムネイルを現行 entry へ渡す。アイコンと同様、リフレッシュで entry が
    /// 置き換わっていたら同一パスの現行 entry を探して引き渡す。</summary>
    private void PublishThumbnail(FileSystemEntry entry, Bitmap? bitmap, CancellationToken token, bool isFinal)
    {
        var target = token.IsCancellationRequested || !IsIconsView
            ? null
            : _entryByPath.GetValueOrDefault(entry.FullPath);
        if (target is null || target.IsThumbnailFinal)
        {
            bitmap?.Dispose();
            return;
        }

        if (bitmap is not null)
        {
            target.SetThumbnail(bitmap, isFinal);
        }
        else if (isFinal)
        {
            // 高解像度が得られなかった項目は、スクロールのたびに再試行しないよう完了扱いにする。
            target.MarkThumbnailFinal();
        }
    }

    [RelayCommand]
    private void ToggleCompactView() => IsCompactView = !IsCompactView;

    /// <summary>フィルタ前の全エントリ（検索の母集合）。</summary>
    private List<FileSystemEntry> _allEntries = new();

    /// <summary>パス→現行エントリの対応表。アイコン/サムネイル読込完了時の線形探索を避ける
    /// （数万件フォルダーでは全件スクロールで累積 O(n²) になるため）。</summary>
    private Dictionary<string, FileSystemEntry> _entryByPath = new(WindowsPathIdentity.Instance);

    /// <summary>_allEntries と _entryByPath を常に同時に差し替える。</summary>
    private void SetAllEntries(List<FileSystemEntry> entries)
    {
        _allEntries = entries;
        var map = new Dictionary<string, FileSystemEntry>(entries.Count, WindowsPathIdentity.Instance);
        foreach (var entry in entries)
        {
            map[entry.FullPath] = entry;
        }
        _entryByPath = map;
    }

    public bool HasNoEntries => Entries.Count == 0;

    private string SortGlyph(string key)
        => SortKey == key ? (SortAscendingFlag ? "\uE70E" : "\uE70D") : "";

    public string NameSortGlyph => SortGlyph(SortKeys.Name);
    public string ModifiedSortGlyph => SortGlyph(SortKeys.Modified);
    public string TypeSortGlyph => SortGlyph(SortKeys.Type);
    public string SizeSortGlyph => SortGlyph(SortKeys.Size);
    public string CreatedSortGlyph => SortGlyph(SortKeys.Created);

    public bool IsSortByName => SortKey == SortKeys.Name;
    public bool IsSortByModified => SortKey == SortKeys.Modified;
    public bool IsSortByCreated => SortKey == SortKeys.Created;
    public bool IsSortByType => SortKey == SortKeys.Type;
    public bool IsSortBySize => SortKey == SortKeys.Size;
    public bool IsSortAscending => SortAscendingFlag;
    public bool IsSortDescending => !SortAscendingFlag;

    private string _currentPath = FileSystemService.ComputerPath;

    /// <summary>現在表示中のパス。FileSystemService.ComputerPath ならドライブ一覧。
    /// 変更通知はサイドバーのツリー同期（MainWindowViewModel）が購読する。</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    private List<FileSystemEntry> _entries = [];

    public IReadOnlyList<FileSystemEntry> Entries => _entries;

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = new();

    public bool IsDetailsView => ViewMode == ViewMode.Details;

    public bool IsListView => ViewMode is ViewMode.List or ViewMode.SmallIcons;

    public bool IsIconsView => ViewMode is ViewMode.ExtraLargeIcons or ViewMode.LargeIcons or ViewMode.MediumIcons;

    /// <summary>小アイコンは横方向へ折り返し、一覧は縦方向を埋めてから次の列へ進む。</summary>
    public Avalonia.Layout.Orientation ListOrientation
        => ViewMode == ViewMode.SmallIcons
            ? Avalonia.Layout.Orientation.Horizontal
            : Avalonia.Layout.Orientation.Vertical;

    /// <summary>
    /// アイコン表示のサイズ（Finder のスライダーと同じ無段階ズーム）。
    /// 表示メニューの 特大 / 大 / 中 はこの値のプリセット。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IconFontSize), nameof(IconItemWidth), nameof(IconCellWidth), nameof(IconCellHeight),
        nameof(IsGalleryView))]
    private double _iconSize = 28;

    public double IconFontSize => IconSize;

    /// <summary>アイコンサイズスライダーを最大（160）まで上げると入る特別モード。
    /// ナビゲーション / プレビューを隠し、1 枚を大きく表示 + 下部フィルムストリップで送る。</summary>
    public bool IsGalleryView => IsIconsView && IconSize >= 159.5;

    /// <summary>アイコンビューのセル幅（アイコンサイズに追従）。</summary>
    public double IconItemWidth => Math.Max(96, IconSize * 1.7);

    /// <summary>ListBoxItem の余白を含む、仮想化パネル上のセル幅。</summary>
    public double IconCellWidth => IconItemWidth + 12;

    /// <summary>アイコン・2 行の名前・ListBoxItem の余白を含む、仮想化パネル上のセル高。</summary>
    public double IconCellHeight => IconSize + 42;

    public bool IsViewExtraLarge => ViewMode == ViewMode.ExtraLargeIcons;
    public bool IsViewLarge => ViewMode == ViewMode.LargeIcons;
    public bool IsViewMedium => ViewMode == ViewMode.MediumIcons;
    public bool IsViewSmall => ViewMode == ViewMode.SmallIcons;
    public bool IsViewList => ViewMode == ViewMode.List;
    public bool IsViewDetails => ViewMode == ViewMode.Details;

    public bool ShowHidden => _options.ShowHidden;

    public bool ShowExtensions => _options.ShowExtensions;

    /// <summary>Chrome の設定タブに相当（ファイル UI の代わりにオプション画面を表示）。</summary>
    public bool IsSettingsTab { get; }

    public bool IsNormalTab => !IsSettingsTab;

    /// <summary>「戻る」履歴（先頭 = 直前）。ナビゲーションボタンの右クリックメニュー用。</summary>
    public IReadOnlyList<string> BackHistory => _back.ToArray();

    /// <summary>「進む」履歴（先頭 = 直後）。</summary>
    public IReadOnlyList<string> ForwardHistory => _forward.ToArray();

    /// <summary>履歴メニューから N 段まとめて戻る / 進む。</summary>
    public void GoHistorySteps(int steps, bool back)
    {
        for (var i = 0; i < steps; i++)
        {
            var command = back ? GoBackCommand : GoForwardCommand;
            if (!command.CanExecute(null))
            {
                break;
            }

            command.Execute(null);
        }
    }

    public TabViewModel(string initialPath, ShellOptions options, bool isSettingsTab = false)
        : this(initialPath, options, folderViewSettings: null, initialViewSettings: null, isSettingsTab)
    {
    }

    /// <summary>表示範囲に入った項目の Windows Shell アイコンをバックグラウンドで取得する。</summary>
    public async Task EnsureWindowsIconAsync(FileSystemEntry entry)
    {
        if (_options.IconSet != FileIconSet.Windows || _isDetached || entry.WindowsIcon is not null
            || !ReferenceEquals(_entryByPath.GetValueOrDefault(entry.FullPath), entry)
            || !_loadingWindowsIcons.TryAdd(entry.FullPath, 0))
        {
            return;
        }

        var token = _lifetimeCts.Token;
        try
        {
            await _windowsIconGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return ShellThumbnailService.TryGetIcon(entry.FullPath, WindowsIconPixelSize);
                }, token).ConfigureAwait(false);

                // 反映だけを UI スレッドへ、入力より低い優先度で流す（サムネイルと同じ理由）。
                Dispatcher.UIThread.Post(
                    () => PublishWindowsIcon(entry, bitmap, token),
                    DispatcherPriority.Background);
            }
            finally
            {
                _windowsIconGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogException($"Windows標準アイコンを読み込めませんでした: {entry.FullPath}", ex);
        }
        finally
        {
            _loadingWindowsIcons.TryRemove(entry.FullPath, out _);
        }
    }

    /// <summary>取得した Windows 標準アイコンを現行 entry へ渡す（UI スレッド）。
    /// 一覧のリフレッシュで entry インスタンスが置き換わっていた場合、旧 entry ごと捨てると
    /// 新 entry 側の読み込みは _loadingWindowsIcons の重複ガードで弾かれた後なので誰もアイコンを
    /// 持てなくなる。同一パスの現行 entry を探して引き渡す。</summary>
    private void PublishWindowsIcon(FileSystemEntry entry, Bitmap? bitmap, CancellationToken token)
    {
        var target = bitmap is null || token.IsCancellationRequested || _options.IconSet != FileIconSet.Windows
            ? null
            : _entryByPath.GetValueOrDefault(entry.FullPath);
        if (bitmap is not null && target is { WindowsIcon: null })
        {
            target.WindowsIcon = bitmap;
        }
        else
        {
            bitmap?.Dispose();
        }
    }

    /// <summary>タブバーへのフォルダードロップ中に挿入位置を示す、表示専用の半透明プレビュータブか。</summary>
    public bool IsDropPreview { get; }

    /// <summary>タブ行の不透明度（プレビュータブは半透明で仮配置される）。</summary>
    public double TabRowOpacity => IsDropPreview ? 0.45 : 1.0;

    /// <summary>ドロップ位置プレビュー用のタブを作る。フォルダー列挙・監視は行わない表示専用。</summary>
    internal static TabViewModel CreateDropPreview(string path, ShellOptions options)
        => new(path, options, folderViewSettings: null, initialViewSettings: null,
            isSettingsTab: false, isDropPreview: true);

    internal TabViewModel(
        string initialPath,
        ShellOptions options,
        FolderViewSettingsService? folderViewSettings,
        FolderViewSettings? initialViewSettings,
        bool isSettingsTab = false,
        bool isDropPreview = false)
    {
        _options = options;
        _folderViewSettings = folderViewSettings;
        GalleryImageTransform = new TransformGroup
        {
            Children = { _galleryCenter, _galleryRotate, _galleryScale, _galleryPan },
        };
        DetailColumns =
        [
            new(this, SortKeys.Name, "Text.Column.Name"),
            new(this, SortKeys.Modified, "Text.Column.Modified"),
            new(this, SortKeys.Created, "Text.Column.Created"),
            new(this, SortKeys.Type, "Text.Column.Type"),
            new(this, SortKeys.Size, "Text.Column.Size"),
        ];
        IsSettingsTab = isSettingsTab;
        IsDropPreview = isDropPreview;
        if (!isSettingsTab && initialViewSettings is not null)
        {
            ApplyFolderViewSettings(initialViewSettings);
        }

        _options.Changed += OnOptionsChanged;
        ClipboardFileService.CutStateChanged += OnCutStateChanged;

        if (isSettingsTab)
        {
            // 設定タブのタイトルだけは固定文言なので、言語切り替え時に付け直す
            // （通常タブのタイトルはフォルダー名で、言語に依存しない）。
            ApplySettingsTabTitle();
            LocalizationService.Changed += OnLocalizationChanged;
        }
        else if (isDropPreview)
        {
            // 表示専用: フォルダー列挙も監視もせず、タイトルとパスだけ整えて仮配置に使う
            CurrentPath = initialPath;
            PathText = initialPath;
            Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(initialPath)) is { Length: > 0 } name
                ? name
                : initialPath;
        }
        else
        {
            // NavigateToAsync が完了するまで CurrentPath が既定値のままだと、その間に発火する
            // SavePinned / SaveOpenTabsAndSettings がまだ空のパスを永続化してしまう。
            // 実際のフォルダー読み込みを待たず、ここで同期的に確定させておく。
            CurrentPath = initialPath;
            NavigateTo(initialPath, record: false);
        }
    }

    /// <summary>タブを閉じるときに共有イベントの購読・リソースを解放する。</summary>
    public void Detach()
    {
        if (_isDetached) return;
        _isDetached = true;
        _lifetimeCts.Cancel();
        _options.Changed -= OnOptionsChanged;
        ClipboardFileService.CutStateChanged -= OnCutStateChanged;
        LocalizationService.Changed -= OnLocalizationChanged;
        _watcherSubscription?.Dispose();
        _watcherSubscription = null;
        _filterDebounceCts?.Cancel();
        _filterDebounceCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        CancelThumbnailScope();
        foreach (var column in DetailColumns) column.Detach();
        DisposeEntryImages(_allEntries);
        ClearPreview();
    }

    private void OnOptionsChanged(object? sender, ShellOptionChangedEventArgs e)
    {
        if (e.Kind == ShellOptionKind.IconSet)
        {
            var enabled = _options.IconSet == FileIconSet.Material;
            var preferLight = enabled && MaterialIconService.IsLightTheme();
            foreach (var entry in _allEntries.Concat(Entries).Distinct())
            {
                entry.UpdateMaterialIconKey(enabled, preferLight);
                if (_options.IconSet != FileIconSet.Windows)
                {
                    entry.DisposeWindowsIcon();
                }
            }

            OnPropertyChanged(nameof(ShowMaterialIcons));
            OnPropertyChanged(nameof(ShowWindowsIcons));
            OnPropertyChanged(nameof(ShowOriginalIcons));
            return;
        }

        OnPropertyChanged(nameof(ShowHidden));
        OnPropertyChanged(nameof(ShowExtensions));
        OnPropertyChanged(nameof(ShowCheckBoxes));
        OnPropertyChanged(nameof(ShowMaterialIcons));
        OnPropertyChanged(nameof(ShowWindowsIcons));
        OnPropertyChanged(nameof(ShowOriginalIcons));
        if (e.Kind != ShellOptionKind.ShowCheckBoxes)
        {
            Refresh();
        }
    }

    /// <summary>切り取り状態の変化を全エントリの半透明表示へ反映する（エクスプローラーと同じ見た目）。</summary>
    private void ApplySettingsTabTitle()
    {
        Title = LocalizationService.Text("Text.Nav.Settings");
        PathText = Title;
    }

    private void OnLocalizationChanged(object? sender, EventArgs e) => ApplySettingsTabTitle();

    private void OnCutStateChanged(object? sender, EventArgs e)
    {
        foreach (var entry in _allEntries)
        {
            entry.IsCut = ClipboardFileService.IsCutPath(entry.FullPath);
        }
    }

    public bool ShowCheckBoxes => _options.ShowCheckBoxes;

    public bool ShowMaterialIcons => _options.IconSet == FileIconSet.Material;

    public bool ShowWindowsIcons => _options.IconSet == FileIconSet.Windows;

    public bool ShowOriginalIcons => _options.IconSet == FileIconSet.Original;

    /// <summary>指定パスへ移動する。失敗時は現状維持でステータスにエラーを出す。</summary>
    public void NavigateTo(string path, bool record = true)
        => _ = NavigateToAsync(path, record);

    /// <summary>タブが選択された時点で、表示中の実フォルダーが引き続き存在するか確認する。</summary>
    public void EnsureCurrentPathAvailable()
        => _ = EnsureCurrentPathAvailableAsync();

    private async Task EnsureCurrentPathAvailableAsync()
    {
        if (_isDetached || IsSettingsTab || CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        var path = CurrentPath;
        try
        {
            var exists = await Task.Run(() => Directory.Exists(path), _lifetimeCts.Token);
            if (!exists && !_isDetached && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                // Exists はアクセス不能時にも false になる。実際の列挙で「存在しない」ことを確認してから
                // NavigateToAsync 側の PC フォールバックを適用する。
                await NavigateToAsync(path, record: false);
            }
        }
        catch (OperationCanceledException)
        {
            // タブ破棄時のキャンセルは正常終了として扱う。
        }
    }

    public async Task NavigateToAsync(string path, bool record = true)
    {
        if (_isDetached) return;

        // 固定タブは現在の階層そのものを表す。更新（同一パスの再読み込み）だけはこのタブ内で行い、
        // フォルダー・パンくず・アドレス入力などからの別階層への移動は所有元へ新規タブとして委譲する。
        if (IsPinned && !IsSettingsTab && !WindowsPathIdentity.Instance.Equals(path, CurrentPath))
        {
            IsEditingPath = false;
            PathText = CurrentPath == FileSystemService.ComputerPath ? "PC" : CurrentPath;
            PinnedNavigationRequested?.Invoke(path);
            return;
        }

        var generation = Interlocked.Increment(ref _navigationGeneration);
        // 同一パスの再読み込み（更新）ではスクロール / 選択を維持する。
        // 選択の保持・復元自体は ReplaceEntries が全経路共通で行うため、ここでは
        // SelectedEntry（アンカー）だけを追加で復元する。
        var preserveSelection = WindowsPathIdentity.Instance.Equals(path, CurrentPath) ? SelectedEntry?.FullPath : null;

        List<FileSystemEntry> entries;
        while (true)
        {
            try
            {
                LastListLoadStartUtc = DateTime.UtcNow;
                entries = await Task.Run(() => FileSystemService.GetEntries(path, _options), _lifetimeCts.Token);
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (DirectoryNotFoundException ex) when (path != FileSystemService.ComputerPath)
            {
                bool currentFolderExists;
                try
                {
                    currentFolderExists = await Task.Run(() => Directory.Exists(path), _lifetimeCts.Token);
                }
                catch (OperationCanceledException) { return; }

                // 列挙中に子項目だけが削除された場合は、表示中のフォルダー自体が消えたとは扱わない。
                if (currentFolderExists)
                {
                    Logger.LogException($"フォルダーを開けませんでした: {path}", ex);
                    StatusText = LocalizationService.Text("Text.Nav.OpenFailed", ex.Message);
                    PathText = CurrentPath;
                    return;
                }

                Logger.LogException($"フォルダーが存在しないため PC に移動します: {path}", ex);
                path = FileSystemService.ComputerPath;
                preserveSelection = null;
            }
            catch (Exception ex)
            {
                Logger.LogException($"フォルダーを開けませんでした: {path}", ex);
                StatusText = LocalizationService.Text("Text.Nav.OpenFailed", ex.Message);
                PathText = CurrentPath;
                return;
            }
        }

        if (_isDetached || generation != _navigationGeneration)
        {
            DisposeEntryImages(entries);
            return;
        }

        if (record && !WindowsPathIdentity.Instance.Equals(CurrentPath, path))
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }

        CurrentPath = path;
        ApplySavedFolderViewSettings(path);
        IsEditingPath = false;
        PathText = path == FileSystemService.ComputerPath ? "PC" : path;
        Title = path == FileSystemService.ComputerPath
            ? "PC"
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : path;

        DisposeEntryImages(_allEntries);
        SetAllEntries(ApplySort(entries).ToList());
        // 移動で検索をリセット（エクスプローラーと同じ）。プロパティ経由だと OnSearchTextChanged
        // → ApplyFilter が二重に走るだけで害はないため素直にプロパティへ代入する。
        _suppressSearchFilter = true;
        SearchText = "";
        _suppressSearchFilter = false;
        ApplyFilter();

        BuildBreadcrumbs(path);
        SelectionText = "";
        UpdateFreeSpace(path);
        SetupWatcher(path);
        _searchCts?.Cancel();
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreateNew));

        if (preserveSelection is not null)
        {
            SelectedEntry = Entries.FirstOrDefault(e =>
                string.Equals(e.FullPath, preserveSelection, StringComparison.OrdinalIgnoreCase));
        }

        // 移動したら前フォルダーのサムネイル読み込みはその場で打ち切る。クラウド同期フォルダー
        // （Google ドライブ等）は1件に数秒かかることがあり、打ち切らないと移動先のサムネイルが
        // 旧フォルダーの待ち行列の後ろに並んでいつまでも表示されない。
        if (IsIconsView)
        {
            ResetThumbnailScope();
        }
        else
        {
            CancelThumbnailScope();
        }

        // ギャラリー表示（フォルダー別ビュー記憶で復元された場合を含む）は常に 1 枚を表示する
        if (IsGalleryView && SelectedEntry is null)
        {
            SelectedEntry = Entries.FirstOrDefault();
        }
    }

    /// <summary>直近でフォルダー別設定を適用したパス。初回ナビゲーション判定に使う。</summary>
    private string? _lastViewSettingsPath;

    private void ApplySavedFolderViewSettings(string path)
    {
        var isInitialNavigation = _lastViewSettingsPath is null;
        var pathChanged = !isInitialNavigation
            && !WindowsPathIdentity.Instance.Equals(_lastViewSettingsPath, path);
        _lastViewSettingsPath = path;

        if (_folderViewSettings?.TryGet(path, out var settings) == true)
        {
            ApplyFolderViewSettings(settings);
            return;
        }

        // 「最後に使った表示・並べ替え」(_defaultFolderViewSettings) は新規タブの既定専用。
        // タブ内の移動で未保存フォルダーへ適用すると直前フォルダーの並べ替えが伝染するため、
        // 移動時は既定の並べ替え（名前・昇順）へ戻す。表示モードとアイコンサイズは
        // タブの連続性としてそのまま維持する。初回ナビゲーション（新規タブ / 復元）では
        // コンストラクターで適用済みの新規タブ既定を保つ。
        if (!pathChanged)
        {
            return;
        }

        _isApplyingFolderViewSettings = true;
        try
        {
            SortKey = SortKeys.Name;
            SortAscendingFlag = true;
        }
        finally
        {
            _isApplyingFolderViewSettings = false;
        }
    }

    private void ApplyFolderViewSettings(FolderViewSettings settings)
    {
        _isApplyingFolderViewSettings = true;
        try
        {
            if (Enum.TryParse<ViewMode>(settings.ViewMode, out var mode))
            {
                ViewMode = mode;
            }

            if (settings.IconSize is >= 24 and <= 160)
            {
                IconSize = settings.IconSize;
            }

            if (settings.SortKey is SortKeys.Name or SortKeys.Modified or SortKeys.Created or SortKeys.Type or SortKeys.Size)
            {
                SortKey = settings.SortKey;
                SortAscendingFlag = settings.SortAscending;
            }
        }
        finally
        {
            _isApplyingFolderViewSettings = false;
        }
    }

    private void SaveFolderViewSettings()
    {
        if (_isApplyingFolderViewSettings || _isDetached || IsSettingsTab
            || _folderViewSettings is null || CurrentPath.Length == 0)
        {
            return;
        }

        _folderViewSettings.Set(CurrentPath, new FolderViewSettings
        {
            ViewMode = ViewMode.ToString(),
            IconSize = IconSize,
            SortKey = SortKey,
            SortAscending = SortAscendingFlag,
        });
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        Interlocked.Increment(ref _searchGeneration);
        if (_suppressSearchFilter || _isDetached) return;

        _filterDebounceCts?.Cancel();
        _filterDebounceCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _filterDebounceCts = cts;
        _ = ApplyFilterDebouncedAsync(cts.Token);
    }

    private async Task ApplyFilterDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            if (token.IsCancellationRequested || _isDetached)
            {
                return;
            }

            // まず現在のフォルダー内を即時絞り込みで表示し、続けてサブフォルダーを含む
            // 検索結果で置き換える（エクスプローラーの検索ボックスと同等の見え方）。
            ApplyFilter();
            if (SearchText.Trim().Length > 0)
            {
                await SearchRecursiveAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 次の入力が来た場合は最後の検索語だけを反映する。
        }
    }

    private static void DisposeEntryImages(IEnumerable<FileSystemEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.DisposeThumbnail();
            entry.DisposeWindowsIcon();
        }
    }

    /// <summary>検索テキストで現在のフォルダー内容を絞り込む。</summary>
    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<FileSystemEntry> filteredQuery = _allEntries;
        if (query.Length > 0)
        {
            filteredQuery = filteredQuery.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = filteredQuery.ToList();

        ReplaceEntries(filtered);

        StatusText = query.Length == 0
            ? LocalizationService.Text("Text.Status.ItemCount", filtered.Count)
            : LocalizationService.Text("Text.Status.ItemCountFiltered", filtered.Count, query);
    }

    /// <summary>一覧をスナップショット単位で置換し、3 つの ListBox へ各 1 回だけ通知する。
    /// どの経路（更新・並べ替え・絞り込み・監視による自動更新）でも選択が飛ばないよう、
    /// 置換前の選択をパスで記憶し、置換後の新インスタンスへ復元する。別フォルダーへの移動では
    /// パスが一致しないため自然に復元なしとなる。</summary>
    private void ReplaceEntries(IEnumerable<FileSystemEntry> entries)
    {
        var previousSelection = _selection.Count > 0
            ? new HashSet<string>(_selection.Select(e => e.FullPath), WindowsPathIdentity.Instance)
            : null;

        SelectedEntry = null;
        if (_selection.Count > 0)
        {
            SetSelection([]);
        }

        _entries = entries as List<FileSystemEntry> ?? entries.ToList();
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(HasNoEntries));

        if (previousSelection is null)
        {
            return;
        }

        var restored = _entries.Where(e => previousSelection.Contains(e.FullPath)).ToList();
        if (restored.Count == 1)
        {
            SelectedEntry = restored[0];
        }
        else if (restored.Count > 1)
        {
            // 複数選択の適用は ListBox（View 側）が持つためイベントで依頼する。
            // ItemsSource バインディングの再構築が選択適用を上書きしないよう、
            // 反映後（次のディスパッチャフレーム）に実行する。
            var replacedEntries = _entries;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDetached && ReferenceEquals(_entries, replacedEntries))
                {
                    SelectionRestoreRequested?.Invoke(this, restored);
                }
            });
        }
    }

    private void UpdateFreeSpace(string path)
    {
        // DriveInfo.AvailableFreeSpace は同期 P/Invoke で、切断中のネットワークドライブでは
        // OS のタイムアウトまで UI スレッドをブロックするため、取得は背景スレッドで行う。
        FreeSpaceText = "";
        if (path == FileSystemService.ComputerPath || Path.GetPathRoot(path) is not { Length: > 0 } root)
        {
            return;
        }

        var generation = _navigationGeneration;
        _ = Task.Run(() =>
        {
            string text;
            try
            {
                text = LocalizationService.Text("Text.Status.FreeSpace", FileSystemEntry.FormatSize(new DriveInfo(root).AvailableFreeSpace));
            }
            catch
            {
                // ネットワークドライブ切断などは表示なしで続行
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDetached && generation == _navigationGeneration)
                {
                    FreeSpaceText = text;
                }
            });
        });
    }

    /// <summary>詳細表示のカラムヘッダークリック（同じ列なら昇順 / 降順をトグル、エクスプローラーと同じ）。</summary>
    [RelayCommand]
    private void SortByColumn(string key)
    {
        if (SortKey == key)
        {
            SortAscendingFlag = !SortAscendingFlag;
        }
        else
        {
            SortKey = key;
            SortAscendingFlag = true;
        }

        ResortEntries();
    }

    /// <summary>ディスクを再列挙せず、現在の母集合と表示中の結果だけを並べ替える。</summary>
    public void ResortEntries()
    {
        var selectedPath = SelectedEntry?.FullPath;
        SetAllEntries(ApplySort(_allEntries).ToList());
        // 検索・フィルターが効いていない通常表示は母集合と同一集合のため、2 回目のソートを省略する
        var sortedVisibleEntries = string.IsNullOrEmpty(SearchText) && Entries.Count == _allEntries.Count
            ? _allEntries
            : (IReadOnlyList<FileSystemEntry>)ApplySort(Entries.ToList()).ToList();
        ReplaceEntries(sortedVisibleEntries);

        if (selectedPath is not null)
        {
            SelectedEntry = Entries.FirstOrDefault(entry =>
                WindowsPathIdentity.Instance.Equals(entry.FullPath, selectedPath));
        }
    }

    /// <summary>並べ替え条件をまとめて変更し、一覧へ 1 回だけ反映する。</summary>
    public void SetSort(string key, bool ascending)
    {
        SortKey = key;
        SortAscendingFlag = ascending;
        ResortEntries();
    }

    private void BuildBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add(new BreadcrumbSegment { Name = "PC", Path = FileSystemService.ComputerPath });

        if (path == FileSystemService.ComputerPath)
        {
            return;
        }

        var root = Path.GetPathRoot(path);
        if (root is not { Length: > 0 })
        {
            return;
        }

        // ボリュームラベル取得（DriveInfo.VolumeLabel）は同期 P/Invoke で、切断ドライブでは
        // UI スレッドを長時間ブロックし得るため、キャッシュ表記で即時構築し背景で最新へ差し替える。
        var rootLabel = DriveLabelCache.TryGetValue(root, out var cachedLabel)
            ? cachedLabel
            : root.TrimEnd(Path.DirectorySeparatorChar);
        Breadcrumbs.Add(new BreadcrumbSegment { Name = rootLabel, Path = root });
        RefreshDriveLabelAsync(root);

        var rest = path[root.Length..].Trim(Path.DirectorySeparatorChar);
        if (rest.Length == 0)
        {
            return;
        }

        var current = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var part in rest.Split(Path.DirectorySeparatorChar))
        {
            current = $"{current}{Path.DirectorySeparatorChar}{part}";
            Breadcrumbs.Add(new BreadcrumbSegment { Name = part, Path = current });
        }
    }

    /// <summary>ドライブ表記（例: "Windows (C:)"）のセッション内キャッシュ。UI スレッドからのみ触る。</summary>
    private static readonly Dictionary<string, string> DriveLabelCache = new(WindowsPathIdentity.Instance);

    /// <summary>ボリュームラベルを背景で取得し、パンくずのルート表記とキャッシュを最新化する。</summary>
    private void RefreshDriveLabelAsync(string root)
    {
        var generation = _navigationGeneration;
        _ = Task.Run(() =>
        {
            string label;
            try
            {
                label = FileSystemService.GetDriveLabel(new DriveInfo(root));
            }
            catch
            {
                // 切断ドライブなどはフォールバック表記のまま
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                DriveLabelCache[root] = label;
                if (!_isDetached && generation == _navigationGeneration
                    && Breadcrumbs.Count > 1 && Breadcrumbs[1].Path == root && Breadcrumbs[1].Name != label)
                {
                    Breadcrumbs[1] = new BreadcrumbSegment { Name = label, Path = root };
                }
            });
        });
    }

    /// <summary>エントリを開く（フォルダーは移動、ファイルは関連付けで起動）。</summary>
    public void Open(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            NavigateTo(entry.FullPath);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Text("Text.Launch.Failed", ex.Message);
        }
    }

    /// <summary>
    /// アドレスバーの入力を確定して移動する。
    /// %VAR% の環境変数展開・~ のホーム展開・ファイルパス（関連付けで起動）にも対応。
    /// </summary>
    public void NavigateToPathText()
    {
        var input = PathText.Trim();
        if (input.Length == 0 || string.Equals(input, "PC", StringComparison.OrdinalIgnoreCase))
        {
            NavigateTo(FileSystemService.ComputerPath);
            return;
        }

        input = Environment.ExpandEnvironmentVariables(input);
        if (input == "~" || input.StartsWith("~\\") || input.StartsWith("~/"))
        {
            input = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                input.TrimStart('~', '\\', '/'));
        }

        if (File.Exists(input))
        {
            try
            {
                Process.Start(new ProcessStartInfo(input) { UseShellExecute = true })?.Dispose();
                PathText = CurrentPath == FileSystemService.ComputerPath ? "PC" : CurrentPath;
            }
            catch (Exception ex)
            {
                StatusText = LocalizationService.Text("Text.Launch.Failed", ex.Message);
            }

            return;
        }

        NavigateTo(input);
    }

    /// <summary>エクスプローラーと同じ規則で並べ替える（フォルダー優先）。</summary>
    private IEnumerable<FileSystemEntry> ApplySort(List<FileSystemEntry> entries)
    {
        var grouped = entries.OrderByDescending(e => e.IsDirectory);
        IOrderedEnumerable<FileSystemEntry> sorted = SortKey switch
        {
            SortKeys.Modified => SortAscendingFlag
                ? grouped.ThenBy(e => e.Modified)
                : grouped.ThenByDescending(e => e.Modified),
            SortKeys.Created => SortAscendingFlag
                ? grouped.ThenBy(e => e.Created)
                : grouped.ThenByDescending(e => e.Created),
            SortKeys.Type => SortAscendingFlag
                ? grouped.ThenBy(e => e.TypeText, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenByDescending(e => e.TypeText, StringComparer.CurrentCultureIgnoreCase),
            SortKeys.Size => SortAscendingFlag
                ? grouped.ThenBy(e => e.Size ?? -1)
                : grouped.ThenByDescending(e => e.Size ?? -1),
            _ => SortAscendingFlag
                ? grouped.ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenByDescending(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        return sorted;
    }

    /// <summary>複数選択の変化を受けてステータスバー・コマンド活性を更新する。</summary>
    public void SetSelection(IReadOnlyList<FileSystemEntry> selection)
    {
        _selection = selection.ToList();
        CutCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();

        if (selection.Count == 0)
        {
            SelectionText = "";
            return;
        }

        var totalSize = selection.Where(e => e.Size is not null).Sum(e => e.Size!.Value);
        var sizeText = selection.Any(e => e.Size is not null) ? $" {FileSystemEntry.FormatSize(totalSize)}" : "";
        var folders = selection.Count(e => e.IsDirectory);
        var files = selection.Count - folders;
        var breakdown = folders > 0 && files > 0 ? " " + LocalizationService.Text("Text.Status.Breakdown", folders, files) : "";
        SelectionText = LocalizationService.Text("Text.Status.ItemsSelected", selection.Count) + sizeText + breakdown;

        if (_previewEnabled && selection.Count > 1)
        {
            PreviewBitmap?.Dispose();
            PreviewBitmap = null;
            PreviewText = "";
            PreviewInfo = SelectionText;
        }
    }

    /// <summary>詳細表示の列の表示 / 非表示を切り替える（ヘッダー右クリック）。</summary>
    [RelayCommand]
    private void ToggleColumn(string key)
    {
        switch (key)
        {
            case SortKeys.Modified:
                ShowColModified = !ShowColModified;
                break;
            case SortKeys.Type:
                ShowColType = !ShowColType;
                break;
            case SortKeys.Size:
                ShowColSize = !ShowColSize;
                break;
            case SortKeys.Created:
                ShowColCreated = !ShowColCreated;
                break;
        }
    }

    public void MoveDetailColumn(DetailColumnViewModel source, DetailColumnViewModel target)
    {
        var sourceIndex = DetailColumns.IndexOf(source);
        var targetIndex = DetailColumns.IndexOf(target);
        if (sourceIndex >= 0 && targetIndex >= 0 && sourceIndex != targetIndex)
            DetailColumns.Move(sourceIndex, targetIndex);
    }

    private bool HasSelection => _selection.Count > 0;

    /// <summary>ドライブは切り取り / 削除 / 名前変更の対象にしない（誤操作防止、エクスプローラー互換）。</summary>
    private bool HasModifiableSelection => _selection.Count > 0 && _selection.All(e => !e.IsDrive);

    private bool HasSingleSelection => _selection.Count == 1 && !_selection[0].IsDrive;

    /// <summary>新規作成が可能か（PC ビューでは不可）。</summary>
    public bool CanCreateNew => CurrentPath != FileSystemService.ComputerPath && !IsSettingsTab;

    [RelayCommand(CanExecute = nameof(HasModifiableSelection))]
    private void Cut()
    {
        if (ClipboardFileService.SetFiles(_selection.Select(e => e.FullPath).ToList(), cut: true))
        {
            StatusText = LocalizationService.Text("Text.Clipboard.Cut", _selection.Count);
            PasteCommand.NotifyCanExecuteChanged();
        }
        else
        {
            StatusText = LocalizationService.Text("Text.Clipboard.WriteFailed");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (ClipboardFileService.SetFiles(_selection.Select(e => e.FullPath).ToList(), cut: false))
        {
            StatusText = LocalizationService.Text("Text.Clipboard.Copied", _selection.Count);
            PasteCommand.NotifyCanExecuteChanged();
        }
        else
        {
            StatusText = LocalizationService.Text("Text.Clipboard.WriteFailed");
        }
    }

    private bool CanPaste => CurrentPath != FileSystemService.ComputerPath && ClipboardFileService.HasFiles();

    /// <summary>ウィンドウのアクティブ化などでクリップボード状態を再評価する。</summary>
    public void NotifyClipboardChanged() => PasteCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync()
    {
        if (CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        var files = ClipboardFileService.GetFiles(out var isCut);
        var hasVirtualFiles = files.Count == 0 && ClipboardFileService.HasVirtualFiles();
        if (files.Count == 0 && !hasVirtualFiles)
        {
            return;
        }

        var dest = CurrentPath;
        if (hasVirtualFiles)
        {
            // RDP の仮想ファイルはローカルパスを持たない。Explorer と同じフォルダー背景の
            // paste verb に IDataObject / FileContents の取得を任せる。
            var pasteInvokedAtUtc = DateTime.UtcNow;
            if (!ShellContextMenuService.InvokeDirectoryBackgroundVerb(0, dest, "paste"))
            {
                StatusText = LocalizationService.Text("Text.Clipboard.VirtualPasteFailed");
                return;
            }

            // Shell の貼り付けは非同期で完了することがあるが、反映はフォルダー監視が正本。
            // 監視が使えないタブだけ、時限の再読み込みを保険として行う。
            foreach (var delayMs in (int[])[500, 2000])
            {
                await Task.Delay(delayMs);
                if (NeedsShellRefreshBackup && LastListLoadStartUtc <= pasteInvokedAtUtc)
                {
                    Refresh();
                }
            }

            return;
        }

        // 同一フォルダーへのコピー貼り付けはエクスプローラーと同じく自動リネーム（"- コピー"）
        var sameDir = !isCut && files.Any(f => WindowsPathIdentity.Instance.Equals(
            Path.GetDirectoryName(f), dest));

        // IFileOperation は同期ブロッキングだが独自の進捗ダイアログを出すため背景スレッドで実行
        var result = await Task.Run(() => FileOperationService.CopyOrMove(files, dest, move: isCut, renameOnCollision: sameDir));
        if (result.IsSuccess && isCut) ClipboardFileService.Clear();
        if (result.IsSuccess) Refresh();
        else if (!result.IsCancelled) StatusText = LocalizationService.Text("Text.Op.PasteFailed", FormatOpError(result.NativeErrorCode));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => DeleteCoreAsync(permanent: false);

    /// <summary>Shift+Delete の完全削除（ごみ箱を経由しない、システム確認あり）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeletePermanent() => DeleteCoreAsync(permanent: true);

    /// <summary>ファイル操作エラーを「エラー 206: パスが長すぎます」の形式に整形する（説明が無いコードは生数値のみ）。</summary>
    private static string FormatOpError(int code)
    {
        var desc = FileOperationService.DescribeError(code);
        return desc.Length > 0
            ? LocalizationService.Text("Text.Error.CodeWithDesc", code, desc)
            : LocalizationService.Text("Text.Error.Code", code);
    }

    private async Task DeleteCoreAsync(bool permanent)
    {
        var targets = _selection.Select(e => e.FullPath).ToList();
        // 削除後はエクスプローラーと同じく隣接項目を選択する
        var anchorIndex = _selection.Count > 0 ? _entries.IndexOf(_selection[0]) : -1;

        var result = await Task.Run(() => FileOperationService.DeleteToRecycleBin(targets, permanent));
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled) StatusText = LocalizationService.Text("Text.Op.DeleteFailed", FormatOpError(result.NativeErrorCode));
            return;
        }
        await NavigateToAsync(CurrentPath, record: false);

        if (anchorIndex >= 0 && Entries.Count > 0)
        {
            SelectedEntry = Entries[Math.Min(anchorIndex, Entries.Count - 1)];
        }
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private void Rename()
    {
        if (_selection.Count == 1)
        {
            RenameRequested?.Invoke(this, _selection[0]);
        }
    }

    /// <summary>名前の変更を確定する（View のダイアログから呼ばれる）。バリデーション付き。</summary>
    public async Task CommitRenameAsync(FileSystemEntry entry, string newName)
    {
        // OK でダイアログを閉じた時点で新規作成の保留状態は終了する。
        // 入力不備で改名できなかった場合も、後の通常リネームのキャンセルで削除されないようにする。
        _pendingNewFolderPaths.Remove(entry.FullPath);

        newName = newName.Trim();
        if (newName.Length == 0 || newName == entry.Name)
        {
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText = LocalizationService.Text("Text.Rename.InvalidChars");
            return;
        }

        var dir = Path.GetDirectoryName(entry.FullPath);
        if (dir is null)
        {
            return;
        }

        var newPath = Path.Combine(dir, newName);
        var isCaseOnlyRename = string.Equals(entry.FullPath, newPath, StringComparison.OrdinalIgnoreCase);
        if (!isCaseOnlyRename && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            StatusText = LocalizationService.Text("Text.Rename.AlreadyExists", newName);
            return;
        }

        if (isCaseOnlyRename)
        {
            // Windows の大文字・小文字だけの変更は同一パス扱いになるため、一時名を経由する。
            var temporary = Path.Combine(dir, $".kiriha-rename-{Guid.NewGuid():N}");
            var first = FileOperationService.Rename(entry.FullPath, temporary);
            if (!first.IsSuccess)
            {
                if (!first.IsCancelled) StatusText = LocalizationService.Text("Text.Op.RenameFailed", FormatOpError(first.NativeErrorCode));
                return;
            }

            // 一時名への改名は既に成功しているため、ここで失敗したら一時名のまま残ってしまう。必ず元へ戻す。
            var second = FileOperationService.Rename(temporary, newPath);
            if (!second.IsSuccess)
            {
                var rollback = FileOperationService.Rename(temporary, entry.FullPath);
                StatusText = rollback.IsSuccess
                    ? LocalizationService.Text("Text.Rename.RevertedToOriginal")
                    : LocalizationService.Text("Text.Rename.StuckAsTemporary", Path.GetFileName(temporary));
                await NavigateToAsync(CurrentPath, record: false);
                return;
            }
        }
        else
        {
            var result = FileOperationService.Rename(entry.FullPath, newPath);
            if (!result.IsSuccess)
            {
                if (!result.IsCancelled) StatusText = LocalizationService.Text("Text.Op.RenameFailed", FormatOpError(result.NativeErrorCode));
                return;
            }
        }
        await NavigateToAsync(CurrentPath, record: false);

        // 変更後の項目を選択し直す
        var renamed = Entries.FirstOrDefault(e => string.Equals(e.FullPath, newPath, StringComparison.OrdinalIgnoreCase));
        if (renamed is not null)
        {
            SelectedEntry = renamed;
        }
    }

    [RelayCommand]
    private void ShowProperties()
    {
        var target = _selection.Count > 0 ? _selection[0].FullPath : CurrentPath;
        if (target != FileSystemService.ComputerPath)
        {
            FileOperationService.ShowProperties(target);
        }
    }

    [RelayCommand]
    private void Share()
    {
        // Explorer の「共有」と同じ ModernSharing ハンドラー（Windows.ModernShare verb）で
        // Windows 標準の共有シートを開く（WinRT プロジェクションに依存しない）。
        var targets = _selection.Where(e => !e.IsDirectory).Select(e => e.FullPath).ToList();
        if (targets.Count == 0)
        {
            StatusText = _selection.Count > 0
                ? LocalizationService.Text("Text.Share.FoldersNotSupported")
                : LocalizationService.Text("Text.Share.SelectFiles");
            return;
        }

        var hwnd = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?
            .MainWindow?.TryGetPlatformHandle()?.Handle ?? 0;
        if (!ShareService.Show(hwnd, targets))
        {
            StatusText = LocalizationService.Text("Text.Share.OpenFailed");
        }
    }

    [RelayCommand]
    private void SetSortKey(string key)
    {
        SetSort(key, SortAscendingFlag);
    }

    [RelayCommand]
    private void SetSortAscending(string ascending)
    {
        SetSort(SortKey, ascending == "True");
    }

    /// <summary>ドロップ / DnD 移動のファイル操作（背景スレッド実行 + 完了後更新）。</summary>
    public async Task DropFilesAsync(IReadOnlyList<string> files, string destDir, bool move)
    {
        if (files.Count == 0 || destDir.Length == 0)
        {
            return;
        }

        // 自分自身の場所へのドロップは無視（エクスプローラーと同じ）
        var effective = files
            .Where(f => !WindowsPathIdentity.Instance.Equals(Path.GetDirectoryName(f), destDir))
            .ToList();
        if (effective.Count == 0)
        {
            return;
        }

        var result = await Task.Run(() => FileOperationService.CopyOrMove(effective, destDir, move));
        if (result.IsSuccess)
        {
            Refresh();
            StatusText = LocalizationService.Text(move ? "Text.Op.Moved" : "Text.Op.Copied", effective.Count);
        }
        else if (!result.IsCancelled)
        {
            StatusText = LocalizationService.Text("Text.Op.Failed", FormatOpError(result.NativeErrorCode));
        }
    }

    /// <summary>新規フォルダーを作成し、選択して即リネーム入力へ（エクスプローラーと同じ）。</summary>
    public void CreateNewFolder()
        => _ = CreateNewFolderAsync();

    private async Task CreateNewFolderAsync()
    {
        if (CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        try
        {
            var path = GetUniquePath(LocalizationService.Text("Text.New.FolderName"), "");
            Directory.CreateDirectory(path);
            _pendingNewFolderPaths.Add(path);
            await NavigateToAsync(CurrentPath, record: false);

            var created = Entries.FirstOrDefault(e => string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase));
            if (created is not null)
            {
                SelectedEntry = created;
                RenameRequested?.Invoke(this, created);
            }
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Text("Text.New.CreateFailed", ex.Message);
        }
    }

    /// <summary>新規フォルダーの名前入力をキャンセルした場合だけ、作成済みの空フォルダーを取り消す。</summary>
    public async Task CancelPendingNewFolderAsync(FileSystemEntry entry)
    {
        if (!_pendingNewFolderPaths.Remove(entry.FullPath) || !Directory.Exists(entry.FullPath))
        {
            return;
        }

        try
        {
            // ダイアログ表示中に内容が追加されていた場合は IOException になり、誤削除しない。
            Directory.Delete(entry.FullPath);
            await NavigateToAsync(CurrentPath, record: false);
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Text("Text.New.UndoFailed", ex.Message);
        }
    }

    /// <summary>ターミナル（Windows Terminal、無ければ cmd）を現在のフォルダーで開く。</summary>
    [RelayCommand]
    private void OpenTerminal()
    {
        if (CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        try
        {
            TrustedProcessLauncher.Start("wt.exe", ["-d", CurrentPath], CurrentPath);
        }
        catch
        {
            try
            {
                TrustedProcessLauncher.Start("cmd.exe", [], CurrentPath);
            }
            catch (Exception ex)
            {
                StatusText = LocalizationService.Text("Text.Launch.TerminalFailed", ex.Message);
            }
        }
    }

    /// <summary>現在のフォルダーをエクスプローラーで開く。</summary>
    [RelayCommand]
    private void OpenInExplorer()
    {
        try
        {
            TrustedProcessLauncher.Start(
                "explorer.exe",
                CurrentPath == FileSystemService.ComputerPath ? [] : [CurrentPath],
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Text("Text.Launch.ExplorerFailed", ex.Message);
        }
    }

    /// <summary>「新規作成」メニューの ShellNew テンプレートからファイルを作成する。</summary>
    public void CreateFromTemplate(NewItemTemplate template)
        => _ = CreateFromTemplateAsync(template);

    private async Task CreateFromTemplateAsync(NewItemTemplate template)
    {
        if (CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        try
        {
            var path = GetUniquePath(LocalizationService.Text("Text.New.ItemName", template.DisplayName), template.Extension);
            switch (template.Kind)
            {
                case NewItemKind.NullFile:
                    File.WriteAllBytes(path, []);
                    break;
                case NewItemKind.Data:
                    File.WriteAllBytes(path, template.Data ?? []);
                    break;
                case NewItemKind.TemplateFile:
                    File.Copy(template.TemplatePath!, path);
                    break;
            }

            await NavigateToAsync(CurrentPath, record: false);
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Text("Text.New.CreateFailed", ex.Message);
        }
    }

    private string GetUniquePath(string baseName, string extension)
    {
        var candidate = Path.Combine(CurrentPath, baseName + extension);
        var i = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(CurrentPath, $"{baseName} ({i}){extension}");
            i++;
        }

        return candidate;
    }

    private bool CanGoBack => _back.Count > 0;

    private bool CanGoForward => _forward.Count > 0;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        _forward.Push(CurrentPath);
        NavigateTo(_back.Pop(), record: false);
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        _back.Push(CurrentPath);
        NavigateTo(_forward.Pop(), record: false);
    }

    [RelayCommand]
    private void GoUp()
    {
        if (CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        var parent = Directory.GetParent(CurrentPath);
        NavigateTo(parent?.FullName ?? FileSystemService.ComputerPath);
    }

    [RelayCommand]
    private void Refresh()
    {
        NavigateTo(CurrentPath, record: false);
    }

    /// <summary>パンくずのセグメントクリックで移動する。</summary>
    [RelayCommand]
    private void NavigateToPath(string path)
    {
        NavigateTo(path);
    }

    [RelayCommand]
    private void SetViewMode(string mode)
    {
        if (Enum.TryParse<ViewMode>(mode, out var value))
        {
            ViewMode = value;
        }
    }

    [RelayCommand]
    private void ToggleShowHidden() => _options.ShowHidden = !_options.ShowHidden;

    [RelayCommand]
    private void ToggleShowExtensions() => _options.ShowExtensions = !_options.ShowExtensions;

    [RelayCommand]
    private void ToggleShowCheckBoxes() => _options.ShowCheckBoxes = !_options.ShowCheckBoxes;

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    partial void OnIsPinnedChanged(bool value)
    {
        if (!value)
        {
            return;
        }

        // 固定時点より前の履歴から別階層へ抜けられないよう、履歴も階層と一緒に固定する。
        _back.Clear();
        _forward.Clear();
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CloseSelf() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
