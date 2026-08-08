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

// 走査 API 側の FileSystemEntry（ref struct）は、このプロジェクトの Models.FileSystemEntry と名前が
// ぶつかるので別名で使う。
using SysEntry = System.IO.Enumeration.FileSystemEntry;

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

    /// <summary>設定画面で決めた「フォルダーの既定表示」を取り出す（表示設定を保存していない
    /// フォルダーへ移ったときに使う）。ドロップ表示用のタブなど、既定を持たない場合は null。</summary>
    private readonly Func<FolderViewSettings>? _defaultViewSettings;
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
        nameof(IsDetailsView), nameof(IsListView), nameof(IsIconsView), nameof(IsTilesView),
        nameof(IconFontSize), nameof(UsesThumbnails),
        nameof(ListOrientation), nameof(IsGalleryView),
        nameof(IsViewExtraLarge), nameof(IsViewLarge), nameof(IsViewMedium),
        nameof(IsViewSmall), nameof(IsViewList), nameof(IsViewDetails), nameof(IsViewTiles),
        nameof(IsViewGallery))]
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

    /// <summary>一覧の特定行までスクロールする要求。スクロールは ListBox が持つ機能なので View へ委ねる
    /// （SelectionRestoreRequested と同じ理由で static イベント）。</summary>
    public static event Action<TabViewModel, RevealRequest>? RevealEntryRequested;

    /// <summary>クリップボード操作の結果をウィンドウ上のトーストで知らせる要求（購読者は MainWindow 1 つ）。</summary>
    public static event Action<TabViewModel, ToastRequest>? ToastRequested;

    /// <summary>一覧の行を見せる要求。Center が true なら画面中央へ、false なら最小移動で収める。</summary>
    /// <param name="Entry">見せたい行。</param>
    /// <param name="Center">一覧の中央へスクロールするか。</param>
    public readonly record struct RevealRequest(FileSystemEntry Entry, bool Center);

    /// <summary>トーストの表示内容。Label は操作名のチップ、Message は本文。</summary>
    /// <param name="Label">操作名（「コピー」など）。</param>
    /// <param name="Message">結果の説明文。</param>
    public readonly record struct ToastRequest(string Label, string Message);

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

    /// <summary>
    /// 詳細表示の列ヘッダーへ掛ける横方向のずらし（一覧の横スクロールに追従させる）。
    ///
    /// ヘッダーは一覧の外（別の Grid 行）にあるので一覧のスクロールには乗らない。
    /// ギャラリーの拡大縮小と同じ理由で、<c>RenderTransform</c> はビジュアルツリーの外に居て
    /// DataContext を継承しないため、X を個別に Binding しても効かない。ここで組み立てた
    /// Transform をまるごと渡し、値の更新はコードビハインドから <see cref="SetDetailHeaderOffset"/> で行う。
    /// </summary>
    public Transform DetailHeaderTransform { get; }

    private readonly TranslateTransform _detailHeaderTranslate = new();

    /// <summary>一覧の横スクロール量をヘッダーへ反映する（コードビハインドの ScrollChanged から）。</summary>
    public void SetDetailHeaderOffset(double offsetX)
        => _detailHeaderTranslate.X = -offsetX;

    /// <summary>詳細表示のカラム幅（ヘッダーの Thumb ドラッグで変更）。</summary>
    [ObservableProperty]
    private double _colNameWidth = 300;

    [ObservableProperty]
    private double _colModifiedWidth = 160;

    [ObservableProperty]
    private double _colTypeWidth = 140;

    [ObservableProperty]
    private double _colSizeWidth = 100;

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

    /// <summary>ステータスバー右側（選択が 1 件のときだけ出す更新日時）。
    /// 複数選択では日時が一意に決まらないので空にする。</summary>
    [ObservableProperty]
    private string _selectionModifiedText = "";

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

    /// <summary>プレビュー画像の伸縮方法。表示サイズちょうどで作った 1 枚は
    /// <see cref="Stretch.Fill"/> と明示サイズで「描画先＝画素数」に合わせて 1:1 で描く
    /// （ここで伸縮させると、鮮鋭化した輪郭が描画時の再サンプリングでまた鈍ってしまう）。
    /// それ以外は従来どおり <see cref="Stretch.Uniform"/> で領域へ収める。</summary>
    [ObservableProperty]
    private Stretch _previewStretch = Stretch.Uniform;

    /// <summary>プレビュー画像を描く論理サイズ。NaN は「指定なし（従来どおり領域に収める）」。
    /// ビットマップの DPI 解釈に頼らず、ここで描画先の大きさを直接決める。</summary>
    [ObservableProperty]
    private double _previewDisplayWidth = double.NaN;

    [ObservableProperty]
    private double _previewDisplayHeight = double.NaN;

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
        CanRotateSelected = ImageRotationService.CanRotate(extension)
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
        OnPropertyChanged(nameof(CanResetGalleryView));
        ScheduleGalleryReloadIfModeChanged();
    }

    /// <summary>表示中の倍率（コントロールバーに出す文字列）。この表示自体が
    /// 「押すと 100% へ戻る」ボタンになっている。</summary>
    public string GalleryZoomText => $"{GalleryZoom * 100:0}%";

    /// <summary>等倍・回転なしの状態から動いている（＝リセットに意味がある）。</summary>
    public bool CanResetGalleryView
        => Math.Abs(GalleryZoom - 1.0) > 0.005 || GalleryDisplayRotation != 0;

    /// <summary>画面の拡大率（125% なら 1.25）。表示画素サイズを求めるのに要る。</summary>
    private double _galleryScaling = 1.0;

    /// <summary>領域サイズが変わったときの再デコードをまとめるタイマー（ドラッグ中に毎回走らせない）。</summary>
    private DispatcherTimer? _viewportReloadTimer;

    /// <summary>メイン画像領域の大きさが変わったときに呼ぶ（拡大の基準点が領域の中心のため）。
    /// 表示サイズでデコードし直す必要もあるので、画面の拡大率も一緒に受け取る。</summary>
    public void SetGalleryViewport(Size size, double scaling)
    {
        var scalingChanged = Math.Abs(_galleryScaling - scaling) > 0.001;
        if (_galleryViewport == size && !scalingChanged)
        {
            return;
        }

        // 1px 未満の揺れで再デコードしない（レイアウトの丸めでも SizeChanged は飛ぶ）。
        var needsReload = scalingChanged
                          || Math.Abs(_galleryViewport.Width - size.Width) >= 1
                          || Math.Abs(_galleryViewport.Height - size.Height) >= 1;

        _galleryViewport = size;
        _galleryScaling = scaling > 0 ? scaling : 1.0;
        ApplyGalleryTransform();

        if (!needsReload || !IsDisplaySizedPreview)
        {
            return;
        }

        // ウィンドウのドラッグリサイズ中は毎フレーム飛んでくるので、落ち着いてから 1 回だけ通す。
        _viewportReloadTimer ??= CreateViewportReloadTimer();
        _viewportReloadTimer.Stop();
        _viewportReloadTimer.Start();
    }

    private DispatcherTimer CreateViewportReloadTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReloadGalleryPreview();
        };
        return timer;
    }

    /// <summary>表示サイズちょうどでデコードする経路が有効か
    /// （＝鮮鋭化 ON・ギャラリー表示・領域サイズが分かっている・等倍表示）。
    ///
    /// 拡大中は除く。拡大は <see cref="GalleryImageTransform"/> で掛けるので、表示サイズで
    /// 作った 1 枚を拡大すると元の解像度より粗くなる。拡大中は元の解像度のまま渡して
    /// 従来どおり描画側に伸ばしてもらう（等倍へ戻したら作り直す）。</summary>
    private bool IsDisplaySizedPreview
        => IsGalleryView
           && ContrastAdaptiveSharpenService.Enabled
           && _galleryViewport is { Width: >= 1, Height: >= 1 }
           && Math.Abs(GalleryZoom - 1.0) < 0.005;

    /// <summary>いま出しているプレビューが表示サイズで作られたものか（拡大の出入りで作り直す判断に使う）。</summary>
    private bool _displaySizedPreviewActive;

    /// <summary>
    /// これから出す 1 枚の描き方を決める。表示サイズで作った画像は、描画先の論理サイズを
    /// 明示して <see cref="Stretch.Fill"/> で描く（画素数と描画先が一致するので実質 1:1 の転送になる）。
    /// <paramref name="bitmap"/> が null、または表示サイズ経路で作られていない場合は従来どおり領域に収める。
    /// </summary>
    private void ApplyPreviewDisplaySize(GalleryImage? image)
    {
        _displaySizedPreviewActive = image is { DisplaySized: true } && IsDisplaySizedPreview;
        if (_displaySizedPreviewActive && image is { } shown)
        {
            // ビットマップは 96dpi のまま（論理サイズ＝画素数）なので、画面の倍率で割った値が
            // 描くべき論理サイズ。Fill と組み合わせると、描画先の物理画素数と画素数が一致する。
            PreviewDisplayWidth = shown.Bitmap.PixelSize.Width / _galleryScaling;
            PreviewDisplayHeight = shown.Bitmap.PixelSize.Height / _galleryScaling;
            PreviewStretch = Stretch.Fill;
            return;
        }

        PreviewDisplayWidth = double.NaN;
        PreviewDisplayHeight = double.NaN;
        PreviewStretch = Stretch.Uniform;
    }

    /// <summary>拡大率が等倍を出入りしたら、その状態に合う 1 枚へ作り直す（連続操作はまとめる）。</summary>
    private void ScheduleGalleryReloadIfModeChanged()
    {
        if (IsDisplaySizedPreview != _displaySizedPreviewActive)
        {
            ScheduleGalleryReload();
        }
    }

    /// <summary>表示中の 1 枚を作り直す。連続操作（リサイズ・スライダー）は最後の 1 回にまとめる。</summary>
    private void ScheduleGalleryReload()
    {
        if (!IsGalleryView)
        {
            return;
        }

        _viewportReloadTimer ??= CreateViewportReloadTimer();
        _viewportReloadTimer.Stop();
        _viewportReloadTimer.Start();
    }

    /// <summary>
    /// ギャラリーで実際に画面へ出る画素サイズ。領域に収める倍率（拡大はしない。XAML の
    /// StretchDirection=DownOnly と同じ）に、画面の拡大率を掛けた大きさになる。
    /// </summary>
    private PixelSize? GalleryDisplayPixelSize(PixelSize source)
    {
        if (!IsDisplaySizedPreview || source.Width < 1 || source.Height < 1)
        {
            return null;
        }

        var fit = Math.Min(_galleryViewport.Width / source.Width, _galleryViewport.Height / source.Height);
        var scale = Math.Min(fit, 1.0) * _galleryScaling;
        var width = (int)Math.Round(source.Width * scale);
        var height = (int)Math.Round(source.Height * scale);
        return width >= 2 && height >= 2 ? new PixelSize(width, height) : null;
    }

    /// <summary>
    /// ギャラリーへ出す 1 枚をデコードする。鮮鋭化が有効なら「デコード → 表示サイズへ縮小拡大
    /// → 鮮鋭化」の順に通し、描画時の再サンプリングが要らない状態で返す
    /// （呼び出し側は <see cref="PreviewStretch"/> を Stretch.None にする）。
    /// </summary>
    /// <summary>
    /// ギャラリーへ出す 1 枚の元画像を作る。RAW と新世代フォーマットはシェルのコーデックに
    /// 現像してもらい、それ以外は Skia でデコードする。どちらの経路も同じ幅を要求するので、
    /// 後段の鮮鋭化・ガンマ・表示サイズ調整は形式を問わず共通に掛かる。
    ///
    /// シェルは要求サイズを外接矩形として扱い、埋め込みプレビューの実寸を超えて拡大はしない。
    /// 実測（30MB の CR2）では要求 1024 と 2560 で所要時間が変わらない（どちらも現像 1 回で
    /// 620〜740ms）ため、ギャラリーでは常に GalleryDecodeWidth を要求してよい。
    /// </summary>
    private static Bitmap? DecodeGallerySource(string path, CancellationToken token)
    {
        if (!IsShellDecodedImage(Path.GetExtension(path).ToLowerInvariant()))
        {
            return ImageDecodeService.TryDecodeToWidth(path, GalleryDecodeWidth, token);
        }

        // シェル呼び出しはキャンセルを受け付けないので、戻ってから世代を確認して捨てる。
        var shell = ShellThumbnailService.TryGetThumbnail(path, GalleryDecodeWidth);
        if (token.IsCancellationRequested)
        {
            shell?.Dispose();
            return null;
        }

        return shell;
    }

    private GalleryImage? DecodeGalleryBitmap(string path, CancellationToken token)
    {
        var bitmap = DecodeGallerySource(path, token);
        if (bitmap is null)
        {
            return null;
        }

        if (!ContrastAdaptiveSharpenService.Enabled)
        {
            return new GalleryImage(GammaAdjustService.Apply(bitmap), DisplaySized: false);
        }

        // 領域サイズは UI スレッドから書かれる値をそのまま読む。ずれても「1 世代前の
        // 大きさで作る」だけで、そのときは SetGalleryViewport のタイマーが作り直す。
        if (GalleryDisplayPixelSize(bitmap.PixelSize) is not { } size)
        {
            return new GalleryImage(
                GammaAdjustService.Apply(ContrastAdaptiveSharpenService.Apply(bitmap)),
                DisplaySized: false);
        }

        // ガンマは見た目の最終調整なので、鮮鋭化のあとに掛ける（画素数は変わらない）。
        var result = GammaAdjustService.Apply(ContrastAdaptiveSharpenService.ApplyScaled(bitmap, size));
        // 縮小拡大に失敗した場合は元の解像度のまま返ってくるので、そのときは従来どおりの描き方にする。
        return new GalleryImage(result, DisplaySized: result.PixelSize == size);
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

        var source = VideoFrame?.Size ?? PreviewBitmap?.PixelSize;
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

    partial void OnGalleryDisplayRotationChanged(double value)
    {
        ApplyGalleryTransform();
        OnPropertyChanged(nameof(CanResetGalleryView));
    }

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

    /// <summary>回転の書き込みを諦めるまでの時間。Exif の書き換えは一瞬で終わるが、
    /// PNG は画素の並べ替えと再圧縮を挟むため、大きな画像でも間に合う長さにしてある。</summary>
    private static readonly TimeSpan RotateTimeout = TimeSpan.FromSeconds(60);

    /// <summary>いま回転の書き込みが走っているファイル。同じ画像が複数のタブで開かれていても
    /// 二重に回さないよう、タブ単位ではなくアプリ全体で 1 本持つ（<see cref="FileUndoService"/> と同じ考え方）。</summary>
    private static readonly ConcurrentDictionary<string, byte> RotatingPaths = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private Task RotateSelectedLeft() => RotateSelectedAsync(clockwise: false);

    [RelayCommand]
    private Task RotateSelectedRight() => RotateSelectedAsync(clockwise: true);

    /// <summary>静止画を無劣化で回転して保存し、表示とサムネイルを作り直す
    /// （JPEG / TIFF は Exif の向きだけ、PNG は画素の並べ替えと再圧縮）。
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

        // タイムアウトで待つのをやめても、書き込み自体は止められない（待たされているのは
        // キャンセルできないファイル open なので、CancellationToken を渡しても抜けられない）。
        // 打ち切った後にもう一度回すと、遅れて完了した 90 度の上にさらに 90 度が乗って
        // 180 度回ってしまうため、同じファイルへの回転が走っている間は次を受け付けない。
        if (!RotatingPaths.TryAdd(path, 0))
        {
            StatusText = LocalizationService.Text("Text.Gallery.RotateFailed");
            return;
        }

        bool rotated;
        var work = Task.Run(() => ImageRotationService.TryRotate(path, clockwise));
        // 打ち切った場合も含め、書き込みが実際に終わった時点で受付を再開する。
        _ = work.ContinueWith(
            completed => RotatingPaths.TryRemove(path, out _),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        try
        {
            // 他プロセス（ウイルス対策等）が oplock を握っていると、書き込み用の open は
            // 例外も出さずに待ち続けることがある。待ちきりにするとコマンドが完了扱いにならず
            // ボタンが二度と押せなくなるため、上限を切って失敗として扱う。
            rotated = await work.WaitAsync(RotateTimeout);
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
        ClearPrefetched();
        StopVideo();
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        PreviewText = "";
        PreviewInfo = "";
    }

    // ===== ギャラリーの先読み =====
    //
    // ホイールやカーソルで隣の画像へ送るたびにその場でデコードを始めると、1 枚ごとに
    // 待ちが入る（このマシンの実測で 5MP の PNG が 40 ms 前後、大きい写真ならもっと）。
    // 表示中の 1 枚を出し終えてから前後 1 枚を裏でデコードしておき、選択が動いたら
    // 待たずに差し替える。先にデコードを始めてしまうと「今出したい 1 枚」と枠を取り合うので、
    // 仕込むのは必ず表示が済んだ後にする。
    //
    // 保持するのは前後 1 枚ずつだけ。ギャラリー解像度のビットマップは 1 枚で 10MB 前後あり、
    // 半径を広げるとメモリの伸びが体感の改善に見合わない。

    /// <summary>ギャラリー表示のデコード幅。先読みと本表示で必ず同じ値を使う。
    ///
    /// 高 DPI（例: 2560x1440 を 125% 表示）では画面幅そのものが 2560 物理画素あり、
    /// 1920 でデコードすると等倍で出せる素材まで 1.33 倍に引き伸ばされてぼける。
    /// 元より大きい幅を要求しても ImageDecodeService 側で元の幅に丸められるので、
    /// 小さい画像が無駄に大きなビットマップになることはない。</summary>
    private const int GalleryDecodeWidth = 2560;

    /// <summary>プレビューペインのデコード幅（ギャラリーと違い、脇に小さく出すだけ）。</summary>
    private const int PreviewPaneDecodeWidth = 480;

    /// <summary>プレビューでデコードを試みるファイルサイズの上限。</summary>
    private const long PreviewSizeLimit = 64 * 1024 * 1024;

    /// <summary>Skia が扱えないため、シェルのコーデックに現像してもらう拡張子か（RAW・新世代フォーマット）。</summary>
    private static bool IsShellDecodedImage(string ext)
        => ShellImageThumbnailExtensions.Contains(ext) || RawThumbnailExtensions.Contains(ext);

    /// <summary>プレビュー・ギャラリーで画像としてデコードを試みる対象か。
    /// サイズ上限を掛けるのは Skia でデコードするものだけ。シェル経由はファイル全体を
    /// managed メモリへ読み込まないので、30MB 級の RAW でも上限を気にする必要がない。</summary>
    private static bool IsPreviewableImage(FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            return false;
        }

        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        return IsShellDecodedImage(ext)
               || (ImageExtensions.Contains(ext) && entry.Size is < PreviewSizeLimit);
    }

    /// <summary>先読み済みのビットマップ（キーは絶対パス）。取り出した時点で所有権も渡す。
    /// 「表示サイズで作ったものか」も一緒に持つ（作った当時の状態でしか判断できないため）。</summary>
    private readonly Dictionary<string, GalleryImage> _prefetched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ギャラリーへ出す 1 枚と、それが表示サイズちょうどで作られたかどうか。</summary>
    /// <param name="Bitmap">表示するビットマップ。</param>
    /// <param name="DisplaySized">画面に出る画素数で作ってあり、Width / Height 指定で 1:1 に描けるか。</param>
    private readonly record struct GalleryImage(Bitmap Bitmap, bool DisplaySized);

    private CancellationTokenSource? _prefetchCts;

    /// <summary>先読み済みなら取り出す。取り出したものはキャッシュから外れ、破棄責任は呼び出し側へ移る。</summary>
    private GalleryImage? TakePrefetched(string path)
    {
        if (!IsGalleryView)
        {
            // プレビューペインはデコード幅が違うので、ギャラリー用の 1 枚は使わない。
            return null;
        }

        return _prefetched.Remove(path, out var image) ? image : null;
    }

    /// <summary>先読みを打ち切り、抱えているビットマップを破棄する。</summary>
    private void ClearPrefetched()
    {
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;

        foreach (var image in _prefetched.Values)
        {
            image.Bitmap.Dispose();
        }

        _prefetched.Clear();
    }

    /// <summary>ギャラリーの表示中画像を読み直す（鮮鋭化の ON/OFF を今見ている画像へ反映するため）。
    /// 先読み済みのビットマップは切り替え前の設定で作られているので一緒に捨てる。</summary>
    public void ReloadGalleryPreview()
    {
        if (!IsGalleryView)
        {
            return;
        }

        // 動画は作り直さない。UpdatePreview は同じファイルでも Open からやり直すので
        // 再生位置が頭へ戻ってしまう。鮮鋭化もガンマも描画時のシェーダーが持っているため、
        // 「もう一度描き直して」と伝えるだけでよい（一時停止中でも反映される）。
        if (IsVideoPreview)
        {
            VideoRenderRevision++;
            return;
        }

        ClearPrefetched();
        UpdatePreview();
    }

    /// <summary>選択中の前後 1 枚を裏でデコードしておく。隣でなくなったものはここで捨てる。</summary>
    private void PrefetchGalleryNeighbors()
    {
        if (!IsGalleryView || SelectedEntry is not { } current)
        {
            ClearPrefetched();
            return;
        }

        var index = _entries.IndexOf(current);
        if (index < 0)
        {
            ClearPrefetched();
            return;
        }

        var targets = new List<FileSystemEntry>(2);
        foreach (var offset in new[] { -1, 1 })
        {
            var neighbor = index + offset;
            if (neighbor >= 0 && neighbor < _entries.Count && IsPrefetchable(_entries[neighbor]))
            {
                targets.Add(_entries[neighbor]);
            }
        }

        // 隣から外れた分は抱えていても使われないので、ここで解放する。
        foreach (var path in _prefetched.Keys.ToList())
        {
            if (!targets.Any(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                _prefetched[path].Bitmap.Dispose();
                _prefetched.Remove(path);
            }
        }

        var pending = targets.Where(entry => !_prefetched.ContainsKey(entry.FullPath)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var previous = _prefetchCts;
        var scope = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _prefetchCts = scope;
        // linked CTS は _lifetimeCts へコールバック登録が残るため、打ち切ったら必ず破棄する。
        previous?.Cancel();
        previous?.Dispose();

        var token = scope.Token;
        foreach (var entry in pending)
        {
            _ = PrefetchAsync(entry.FullPath, scope, token);
        }
    }

    /// <summary>この項目をギャラリーの先読み対象にできるか（動画・巨大ファイル・フォルダーは除く）。
    /// RAW と新世代フォーマットも対象に含める。シェルの現像は 1 枚あたり 0.6 秒ほどかかるので、
    /// Skia でデコードする通常の画像よりむしろ先読みの効果が大きい。</summary>
    private static bool IsPrefetchable(FileSystemEntry entry)
        => IsPreviewableImage(entry);

    private async Task PrefetchAsync(string path, CancellationTokenSource scope, CancellationToken token)
    {
        try
        {
            // ConfigureAwait(false) を外すと再開が Normal 優先度になり、利用者のクリックより前に
            // 割り込む（Views/MainWindow.axaml.cs のサムネイル読み込みと同じ理由）。
            var image = await Task.Run(() => DecodeGalleryBitmap(path, token), token)
                .ConfigureAwait(false);
            if (image is not { } decoded)
            {
                return;
            }

            Dispatcher.UIThread.Post(
                () =>
                {
                    // 待っている間に世代が変わっていたら（選択が動いた・ギャラリーを出た）捨てる。
                    if (!ReferenceEquals(scope, _prefetchCts) || _prefetched.ContainsKey(path))
                    {
                        decoded.Bitmap.Dispose();
                        return;
                    }

                    _prefetched[path] = decoded;
                },
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // 選択が動いただけなので何もしない。
        }
        catch (Exception ex)
        {
            Logger.Log($"ギャラリーの先読みに失敗: {path} ({ex.Message})", LogLevel.Debug);
        }
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
    private VideoFrame? _videoFrame;

    /// <summary>
    /// 鮮鋭化設定のように、フレームが変わらなくても描き直したいときに増やす値。
    /// <see cref="Controls.VideoFrameView"/> がこれを見て再描画する
    /// （一時停止中は新しいフレームが来ないので、これが無いと設定変更が反映されない）。
    /// </summary>
    [ObservableProperty]
    private int _videoRenderRevision;

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

    /// <summary>早送り・巻き戻しの 1 回あたりの秒数（設定 &gt; 表示 で変更。全タブ共通）。</summary>
    public static double SeekStepSeconds { get; set; } = 1.0;

    /// <summary>
    /// 一時停止中のコマ送り幅（秒）。Media Engine には「1 フレーム進める」API が無いので、
    /// 一般的な 30fps 相当の時間だけ動かす（＝実フレーム境界には合わせない）。
    /// 一時停止中のシークは EventSeeked で強制的に絵を取り直すので、止まったまま絵だけが変わる。
    /// </summary>
    private const double FrameStepSeconds = 1.0 / 30.0;

    /// <summary>コントロールバーの「巻き戻し」。設定の秒数だけ戻す。</summary>
    [RelayCommand]
    private void SeekVideoBackward() => SeekVideoBy(-SeekStepSeconds);

    /// <summary>コントロールバーの「早送り」。設定の秒数だけ進める。</summary>
    [RelayCommand]
    private void SeekVideoForward() => SeekVideoBy(SeekStepSeconds);

    /// <summary>一時停止中のコマ送り。<paramref name="direction"/> は -1 で戻る、+1 で進む。</summary>
    public void StepVideoFrame(int direction) => SeekVideoBy(direction * FrameStepSeconds);

    /// <summary>
    /// ギャラリーで動画を見ているときの ← / → 。再生中は早送り・巻き戻し、
    /// 一時停止中はコマ送りへ割り当てる（画像送りは filmstrip のクリックとナビボタンで行う）。
    /// 扱ったら true を返す。
    /// </summary>
    public bool TryHandleVideoArrow(int direction)
    {
        if (!IsGalleryView || !IsVideoPreview || _video is null)
        {
            return false;
        }

        if (IsVideoPlaying)
        {
            SeekVideoBy(direction * SeekStepSeconds);
        }
        else
        {
            StepVideoFrame(direction);
        }

        return true;
    }

    /// <summary>ガンマ補正（1.0 で無変換）。全タブ・静止画・動画で共通。</summary>
    public double GalleryGamma
    {
        get => GammaAdjustService.Gamma;
        set
        {
            var clamped = Math.Clamp(value, GammaAdjustService.Minimum, GammaAdjustService.Maximum);
            if (Math.Abs(clamped - GammaAdjustService.Gamma) < 0.0005)
            {
                return;
            }

            GammaAdjustService.Gamma = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GalleryGammaText));
            OnPropertyChanged(nameof(CanResetGalleryGamma));
            // 動画は描画時のシェーダーが持つので、バインディング経由で即座に反映される。
            // 静止画は作り直しが要るので、スライダーのドラッグ中に何度も走らせないよう
            // まとめて 1 回だけ通す。
            ScheduleGalleryReload();
        }
    }

    /// <summary>コントロールバーに出すガンマ値。</summary>
    public string GalleryGammaText => $"γ {GalleryGamma:0.00}";

    /// <summary>ガンマが既定値から動いている（＝リセットに意味がある）。</summary>
    public bool CanResetGalleryGamma => !GammaAdjustService.IsNeutral;

    /// <summary>ガンマを 1.00 へ戻す。</summary>
    [RelayCommand]
    private void ResetGalleryGamma() => GalleryGamma = GammaAdjustService.Neutral;

    /// <summary>キー操作 1 回ぶんのガンマ変化量。スライダーの範囲（0.4〜2.5）を約 40 段で動かす。</summary>
    private const double GammaKeyStep = 0.05;

    /// <summary>ギャラリーの ↑ / ↓ からガンマを 1 段動かす。</summary>
    public void AdjustGalleryGamma(int direction)
    {
        if (!IsGalleryView)
        {
            return;
        }

        GalleryGamma += direction * GammaKeyStep;
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
            ClearPrefetched();
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
            // 動画の隣に静止画が並んでいることは珍しくないので、ここでも先読みは仕込む。
            PrefetchGalleryNeighbors();
            return;
        }

        try
        {
            // RAW も新世代フォーマットも「ギャラリーの静止画」としては通常の画像と同じ扱いにする。
            // 経路が違うのはデコード元（Skia かシェルのコーデックか）だけで、解像度・鮮鋭化・
            // ガンマ・先読みは共通に効かせる。
            var isShellImage = IsShellDecodedImage(ext);
            if (isShellImage || (ImageExtensions.Contains(ext) && entry.Size is < PreviewSizeLimit))
            {
                // 先読み済みならデコードを待たずにそのまま出す（ホイール送りの待ち時間が消える）。
                var image = TakePrefetched(entry.FullPath);
                if (image is null)
                {
                    // ギャラリーは表示サイズで作り込む経路、プレビューペインは従来どおり幅指定だけ。
                    image = IsGalleryView
                        ? await Task.Run(() => DecodeGalleryBitmap(entry.FullPath, cts.Token), cts.Token)
                        : await Task.Run(
                            () => isShellImage
                                ? ShellThumbnailService.TryGetThumbnail(entry.FullPath, PreviewPaneDecodeWidth)
                                : ImageDecodeService.TryDecodeToWidth(entry.FullPath, PreviewPaneDecodeWidth, cts.Token),
                            cts.Token) is { } plain
                            ? new GalleryImage(plain, DisplaySized: false)
                            : null;
                    if (cts.IsCancellationRequested)
                    {
                        image?.Bitmap.Dispose();
                        return;
                    }
                }

                if (image is { } shown)
                {
                    var bmp = shown.Bitmap;
                    PreviewBitmap?.Dispose();
                    ApplyPreviewDisplaySize(shown);
                    PreviewBitmap = bmp;
                    PreviewText = "";
                    // 画像は寸法も表示する
                    PreviewInfo = $"{entry.Name}\n{entry.TypeText}  {entry.SizeText}  {bmp.PixelSize.Width}×{bmp.PixelSize.Height}\n" + LocalizationService.Text("Text.Tooltip.Modified", entry.ModifiedText);
                    // 表示が済んでから隣を仕込む（今出す 1 枚とデコードを取り合わせない）。
                    PrefetchGalleryNeighbors();
                    return;
                }

                // 読み取り自体に失敗した場合（コーデック未導入、クラウドドライブの瞬断など）は
                // 情報表示のみへフォールスルー
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

    private void OnObservedDirectoryChanged(DirectoryChangeBatch batch)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDetached || SearchText.Length > 0)
            {
                return;
            }

            // 差分で追随できない通知（監視の復帰・大量変更）だけ、従来どおり全体を読み直す。
            // 最後のファイルシステムイベントより後に列挙を開始済みなら、その読み込みが
            // 変更を反映済みなので読み直さない（操作直後の明示 Refresh との二重走行防止）。
            if (batch.NeedsFullReload)
            {
                if (LastListLoadStartUtc <= batch.LastEventUtc)
                {
                    NavigateTo(CurrentPath, record: false);
                }

                return;
            }

            _ = ApplyDirectoryChangesAsync(batch);
        });
    }

    /// <summary>
    /// フォルダー監視が知らせてきた変更を、変わった項目だけに適用する。
    ///
    /// 以前はどんな変更でもフォルダー全体を読み直していたため、1 ファイル消えただけでも
    /// 全行の再構築（ItemsSource の差し替え）が起きて、進行中のクリック・ダブルクリックが
    /// 巻き添えで落ちていた。ここでは変化した項目の情報だけを取り直し、増減した行だけを
    /// 一覧へ足し引きするので、他の行のコンテナには一切触れない。
    /// </summary>
    private async Task ApplyDirectoryChangesAsync(DirectoryChangeBatch batch)
    {
        _pendingChangeBatches.Enqueue(batch);
        if (_isApplyingChangeBatches)
        {
            return;
        }

        _isApplyingChangeBatches = true;
        try
        {
            while (_pendingChangeBatches.Count > 0)
            {
                await ApplyDirectoryChangeBatchAsync(_pendingChangeBatches.Dequeue());
            }
        }
        finally
        {
            _isApplyingChangeBatches = false;
        }
    }

    /// <summary>適用待ちの監視バッチ。enqueue も dequeue も UI スレッドからしか起きない
    /// （<see cref="OnObservedDirectoryChanged"/> の Post 内で開始し、await の再開も UI スレッド）ため、
    /// ロックは要らない。</summary>
    private readonly Queue<DirectoryChangeBatch> _pendingChangeBatches = new();

    private bool _isApplyingChangeBatches;

    /// <summary>1 バッチぶんの変更を適用する。呼び出しは
    /// <see cref="ApplyDirectoryChangesAsync"/> が到着順に直列化する。</summary>
    private async Task ApplyDirectoryChangeBatchAsync(DirectoryChangeBatch batch)
    {
        var path = CurrentPath;
        if (path == FileSystemService.ComputerPath)
        {
            // ドライブ一覧はファイル単位の変更と対応しないため、従来どおり読み直す
            NavigateTo(path, record: false);
            return;
        }

        var generation = _navigationGeneration;
        var options = _options;
        var targets = batch.Changes
            .Where(change => IsDirectChild(path, change.FullPath))
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        List<(DirectoryChange Change, FileSystemEntry? Fresh)> resolved;
        try
        {
            // 属性・サイズの取得は同期 I/O（切断中のネットワークパスではブロックする）なので背景で行う。
            //
            // 削除通知でも Kind を信じずに実状を見る。イベントが届いてからここへ来るまでに
            // 同名で作り直されていることがあり（上書き保存・ビルド出力）、Kind を信じて
            // 無条件に「消えた」とすると、ディスク上に在る行を一覧から落としてしまう。
            // 存在しなければ TryCreateEntry が null を返すので、結果は従来と同じ。
            resolved = await Task.Run(
                () => targets
                    .Select(change => (change, FileSystemService.TryCreateEntry(change.FullPath, options)))
                    .ToList(),
                _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_isDetached || generation != _navigationGeneration || !WindowsPathIdentity.Instance.Equals(path, CurrentPath))
        {
            return;
        }

        ApplyResolvedChanges(resolved);
    }

    /// <summary>取得し直した内容を母集合へ反映し、変化があれば並べ替え・絞り込みを通して一覧へ出す。</summary>
    private void ApplyResolvedChanges(List<(DirectoryChange Change, FileSystemEntry? Fresh)> resolved)
    {
        var all = new List<FileSystemEntry>(_allEntries);
        var changed = false;

        foreach (var (change, fresh) in resolved)
        {
            var existing = _entryByPath.GetValueOrDefault(change.FullPath);
            if (fresh is null)
            {
                // 消えた、あるいは隠し属性が付いて表示対象外になった
                if (existing is null || !all.Remove(existing))
                {
                    continue;
                }

                existing.DisposeThumbnail();
                existing.DisposeWindowsIcon();
                changed = true;
                continue;
            }

            if (existing is null)
            {
                all.Add(fresh);
                changed = true;
                continue;
            }

            if (!existing.IsSameRowAs(fresh))
            {
                // 表示名や種別が変わった行は作り直す（同名で上書きされた場合など）
                var index = all.IndexOf(existing);
                if (index < 0)
                {
                    continue;
                }

                all[index] = fresh;
                existing.DisposeThumbnail();
                existing.DisposeWindowsIcon();
                changed = true;
                continue;
            }

            if (!existing.UpdateFrom(fresh))
            {
                continue;
            }

            // 中身が変わったのでサムネイルを読み直す。行は作り直さないためビューからの
            // 再要求が起きず、ここから明示的に走らせる（いま出ている画像は差し替わるまで残る）。
            changed = true;
            if (UsesThumbnails && existing.HasThumbnail)
            {
                existing.InvalidateThumbnail();
                _ = EnsureThumbnailAsync(existing);
            }
        }

        if (!changed)
        {
            return;
        }

        SetAllEntries(ApplySort(all).ToList());
        ApplyFilter();
        ReconcileSelectionWithEntries();
    }

    /// <summary>
    /// 監視差分で消えた行を、選択の側からも取り除く。
    ///
    /// 選択の実体は ListBox が持ち、通常は行が消えた時点で SelectionChanged →
    /// <see cref="SetSelection"/> と伝わる。ところが選択中でないタブには ListBox 自体が無く
    /// （タブの中身は SelectedTab の DataTemplate で作られるため）、裏で監視だけが進む。
    /// その間に選択中のファイルが外部から消されると、タブへ戻ってきたときに一覧からは消えているのに
    /// 「1 個の項目を選択」だけが残り、切り取り / 削除 / 名前変更が存在しないパスへ向く（実測で再現）。
    /// </summary>
    private void ReconcileSelectionWithEntries()
    {
        var alive = new HashSet<FileSystemEntry>(_allEntries);
        if (SelectedEntry is { } anchor && !alive.Contains(anchor))
        {
            SelectedEntry = null;
        }

        if (_selection.Count > 0 && _selection.Exists(e => !alive.Contains(e)))
        {
            SetSelection(_selection.Where(alive.Contains).ToList());
        }
    }

    /// <summary>path が parent の直下の項目か（監視は非再帰なので通常は真だが、念のため確かめる）。</summary>
    private static bool IsDirectChild(string parent, string path)
    {
        try
        {
            return Path.GetDirectoryName(path) is { Length: > 0 } directory
                   && WindowsPathIdentity.Instance.Equals(directory, parent);
        }
        catch
        {
            return false;
        }
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

        if (UsesThumbnails)
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
        // サイズスライダーはギャラリーの出入りに関与しない（2026-08-06 以降は ViewMode が唯一の切り替え）。
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

    // 列幅もフォルダーごとに覚える。ヘッダーの Thumb ドラッグ中は高頻度で届くが、
    // FolderViewSettingsService 側が 750ms の遅延保存でまとめるのでここでは素直に呼ぶ。
    partial void OnColNameWidthChanged(double value) => SaveFolderViewSettings();

    partial void OnColModifiedWidthChanged(double value) => SaveFolderViewSettings();

    partial void OnColCreatedWidthChanged(double value) => SaveFolderViewSettings();

    partial void OnColTypeWidthChanged(double value) => SaveFolderViewSettings();

    partial void OnColSizeWidthChanged(double value) => SaveFolderViewSettings();

    public async Task EnsureThumbnailAsync(FileSystemEntry entry)
    {
        // 安価な判定を先に済ませる。このメソッドは EffectiveViewportChanged から
        // 1 項目あたりレイアウト中に何度も呼ばれるため、対象外のときに拡張子判定まで
        // 走らせると（詳細・一覧表示では毎回まるごと無駄になる）無視できない回数になる。
        if (!UsesThumbnails || _isDetached || entry.IsDirectory || entry.IsThumbnailFinal
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
        var target = token.IsCancellationRequested || !UsesThumbnails
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
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(IsComputerRoot));
                OnPropertyChanged(nameof(CanChangeViewMode));
                OnPropertyChanged(nameof(CanCopyPath));
                OnPropertyChanged(nameof(CanCopyFolderPath));
            }
        }
    }

    /// <summary>「PC」（ドライブ一覧）を表示中か。</summary>
    public bool IsComputerRoot => CurrentPath == FileSystemService.ComputerPath;

    /// <summary>
    /// 表示方法を切り替えられるか。「PC」はドライブの名前・空き容量・使用率バーを見る場所なので
    /// 並べて表示に固定し、メニュー・Ctrl+ホイールのどちらからも変えられないようにする。
    /// </summary>
    public bool CanChangeViewMode => !IsComputerRoot && !IsSettingsTab;

    private readonly FileEntryCollection _entries = new();

    /// <summary>表示中の一覧。増減は差分で通知されるので、1 件の変化で全行が作り直されることはない
    /// （<see cref="ReplaceEntries"/> と <see cref="FileEntryCollection"/> を参照）。</summary>
    public FileEntryCollection Entries => _entries;

    /// <summary>Entries を差し替えた回数。非同期処理が「自分が見ていた一覧のままか」を確かめるのに使う。</summary>
    private int _entriesRevision;

    public ObservableCollection<BreadcrumbSegment> Breadcrumbs { get; } = new();

    public bool IsDetailsView => ViewMode == ViewMode.Details;

    public bool IsListView => ViewMode is ViewMode.List or ViewMode.SmallIcons;

    public bool IsIconsView => ViewMode is ViewMode.ExtraLargeIcons or ViewMode.LargeIcons or ViewMode.MediumIcons;

    /// <summary>エクスプローラーの「並べて表示」。サイズはスライダーに追従せず固定（本家と同じ）。</summary>
    public bool IsTilesView => ViewMode == ViewMode.Tiles;

    /// <summary>サムネイルを読み込む表示か。並べて表示も 48px のサムネイルを出す（エクスプローラーと同じ）。
    /// ギャラリーは下部フィルムストリップがサムネイルそのものなので、当然ここに含める。</summary>
    public bool UsesThumbnails => IsIconsView || IsTilesView || IsGalleryView;

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
        nameof(IconFontSize), nameof(IconItemWidth), nameof(IconCellWidth), nameof(IconCellHeight))]
    private double _iconSize = 28;

    public double IconFontSize => IconSize;

    /// <summary>ギャラリー表示か。ナビゲーション / プレビューを隠し、1 枚を大きく表示して
    /// 下部フィルムストリップで送る。
    ///
    /// 以前は「アイコン表示 かつ サイズスライダーが最大」という派生状態だったが、
    /// スライダーを一番右まで動かしただけで意図せず入ってしまうため、
    /// ステータスバー / 表示メニューから選ぶ独立した <see cref="ViewMode"/> にした（2026-08-06）。
    /// スライダーは <see cref="IsIconsView"/> のときだけ出るので、ギャラリー中は表示されない。</summary>
    public bool IsGalleryView => ViewMode == ViewMode.Gallery;

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
    public bool IsViewTiles => ViewMode == ViewMode.Tiles;
    public bool IsViewGallery => ViewMode == ViewMode.Gallery;

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
        bool isDropPreview = false,
        Func<FolderViewSettings>? defaultViewSettings = null)
    {
        _options = options;
        _folderViewSettings = folderViewSettings;
        _defaultViewSettings = defaultViewSettings;
        GalleryImageTransform = new TransformGroup
        {
            Children = { _galleryCenter, _galleryRotate, _galleryScale, _galleryPan },
        };
        DetailHeaderTransform = _detailHeaderTranslate;
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

        // 通常タブもタイトル以外に言語依存の表示名（OpenTerminalText）を持つので購読する。
        // 解除は Detach が無条件に行う（ドロップ仮タブも含め、破棄時に必ず通る）。
        LocalizationService.Changed += OnLocalizationChanged;

        if (isSettingsTab)
        {
            // 設定タブのタイトルだけは固定文言なので、言語切り替え時に付け直す
            // （通常タブのタイトルはフォルダー名で、言語に依存しない）。
            ApplySettingsTabTitle();
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
        _pendingChangeBatches.Clear();
        _filterDebounceCts?.Cancel();
        _filterDebounceCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _selectionSizeCts?.Cancel();
        _selectionSizeCts?.Dispose();
        _selectionSizeCts = null;
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

    private void OnLocalizationChanged(object? sender, EventArgs e)
    {
        if (IsSettingsTab)
        {
            ApplySettingsTabTitle();
        }

        OnPropertyChanged(nameof(OpenTerminalText));
    }

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

                // 想定内の代替処理（未マウントのクラウドドライブ等）。ERROR + スタックで残すと
                // 実害のある失敗と見分けが付かなくなるため、種別とメッセージだけを警告で残す。
                Logger.Log($"フォルダーが存在しないため PC に移動します: {path}: {ex.GetType().Name}", LogLevel.Warning);
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

        // 同一フォルダーの読み直し（フォルダー監視による自動更新・F5・オプション変更）かどうか。
        // 移動と違って表示は基本そのままなので、一覧・パンくず・選択・サムネイルを作り直さない。
        var isReload = WindowsPathIdentity.Instance.Equals(path, CurrentPath);

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

        if (isReload)
        {
            MergeReloadedEntries(entries);
        }
        else
        {
            DisposeEntryImages(_allEntries);
        }

        SetAllEntries(ApplySort(entries).ToList());
        // 移動で検索をリセット（エクスプローラーと同じ）。プロパティ経由だと OnSearchTextChanged
        // → ApplyFilter が二重に走るだけで害はないため素直にプロパティへ代入する。
        _suppressSearchFilter = true;
        SearchText = "";
        _suppressSearchFilter = false;
        ApplyFilter();

        // 読み直しではパンくずも選択の内訳も変わらない。作り直すとパンくずが毎回組み直されて
        // ちらつき、選択が残っているのに「n 個の項目を選択」だけ消える。
        if (!isReload || Breadcrumbs.Count == 0)
        {
            BuildBreadcrumbs(path);
        }

        if (!isReload)
        {
            // 集計中のフォルダーサイズが後から届いて、消したはずの選択情報を書き戻さないようにする
            _selectionSizeCts?.Cancel();
            _selectionSizeCts?.Dispose();
            _selectionSizeCts = null;
            SelectionText = "";
            SelectionModifiedText = "";
        }

        SetupWatcher(path);
        _searchCts?.Cancel();
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        OpenTerminalCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreateNew));

        if (preserveSelection is not null)
        {
            SelectedEntry = Entries.FirstOrDefault(e =>
                string.Equals(e.FullPath, preserveSelection, StringComparison.OrdinalIgnoreCase));
        }

        // 移動したら前フォルダーのサムネイル読み込みはその場で打ち切る。クラウド同期フォルダー
        // （Google ドライブ等）は1件に数秒かかることがあり、打ち切らないと移動先のサムネイルが
        // 旧フォルダーの待ち行列の後ろに並んでいつまでも表示されない。
        // 逆に同一フォルダーの読み直しでは打ち切らない。一覧を作り直さなくなったぶん
        // ビューからの再要求も起きないため、打ち切ると読み込み中だった項目が空欄のまま残る。
        if (UsesThumbnails)
        {
            if (!isReload || _thumbnailScope is null)
            {
                ResetThumbnailScope();
            }
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

        // 「PC」は並べて表示に固定する。適用フラグを立てたまま書くので、
        // フォルダー別の記憶も既定表示も作られない（戻ったときは元のフォルダーの記憶が効く）。
        if (path == FileSystemService.ComputerPath)
        {
            _isApplyingFolderViewSettings = true;
            try
            {
                ViewMode = ViewMode.Tiles;
            }
            finally
            {
                _isApplyingFolderViewSettings = false;
            }

            return;
        }

        if (_folderViewSettings?.TryGet(path, out var settings) == true)
        {
            ApplyFolderViewSettings(settings);
            return;
        }

        // 表示設定を保存していないフォルダーは、設定画面で決めた既定（表示方法・並べ替え・列幅）に従う。
        // 直前のフォルダーの状態を持ち越すと「フォルダーごとに覚える」設定と区別が付かなくなるため、
        // ここで必ず既定へ戻す。初回ナビゲーション（新規タブ / 復元）ではコンストラクターで
        // 同じ既定を適用済みなので何もしない。
        if (!pathChanged)
        {
            return;
        }

        if (_defaultViewSettings?.Invoke() is { } defaults)
        {
            ApplyFolderViewSettings(defaults);
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

    /// <summary>フォルダー別の記憶（または設定画面の既定）をこのタブへ流し込む。
    /// 適用中は保存を止めるので、既定を当てただけでフォルダーの記憶が作られることはない。</summary>
    internal void ApplyFolderViewSettings(FolderViewSettings settings)
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

            ApplyColumnWidths(settings.ColumnWidths);
        }
        finally
        {
            _isApplyingFolderViewSettings = false;
        }
    }

    /// <summary>保存済みの列幅を反映する。持っていない列は今の幅のままにする
    /// （新しい列を追加したときに 0 幅で潰れないようにするため）。</summary>
    private void ApplyColumnWidths(Dictionary<string, double>? widths)
    {
        if (widths is null || widths.Count == 0)
        {
            return;
        }

        foreach (var column in DetailColumns)
        {
            if (widths.TryGetValue(column.Key, out var width) && width is > 0 and <= 2000)
            {
                column.Width = width;
            }
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
            ColumnWidths = DetailColumns.ToDictionary(column => column.Key, column => column.Width),
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

    /// <summary>同一フォルダーの読み直し結果を、表示中の項目へ合流させる（entries を直接書き換える）。
    ///
    /// 毎回すべて作り直すと、読み込み済みのサムネイル / シェルアイコンを捨てたうえで一覧を
    /// 丸ごと差し替えることになり、フォルダー監視が発火するたびに全ファイルのアイコンが
    /// 一斉に消えて読み直される（＝一覧が点滅する）。同じファイルを指す行は既存インスタンスを
    /// 使い回して値だけその場で更新し、増減した行だけを実際の差し替えにする。
    ///
    /// 使い回さなかった（消えた・作り直した）旧項目の画像だけをここで解放する。</summary>
    private void MergeReloadedEntries(List<FileSystemEntry> entries)
    {
        var previousByPath = _entryByPath;
        if (previousByPath.Count == 0)
        {
            return;
        }

        var reused = new HashSet<string>(previousByPath.Count, WindowsPathIdentity.Instance);
        for (var i = 0; i < entries.Count; i++)
        {
            var fresh = entries[i];
            if (!previousByPath.TryGetValue(fresh.FullPath, out var previous) || !previous.IsSameRowAs(fresh))
            {
                continue;
            }

            reused.Add(previous.FullPath);
            entries[i] = previous;
            if (!previous.UpdateFrom(fresh) || !UsesThumbnails || !previous.HasThumbnail)
            {
                continue;
            }

            // 中身が変わったので読み直す。行は作り直さないためビューからの再要求が起きず、
            // ここから明示的に走らせる。いま出ている画像は差し替わるまで残る。
            previous.InvalidateThumbnail();
            _ = EnsureThumbnailAsync(previous);
        }

        foreach (var previous in _allEntries)
        {
            if (!reused.Contains(previous.FullPath))
            {
                previous.DisposeThumbnail();
                previous.DisposeWindowsIcon();
            }
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
        var next = entries as List<FileSystemEntry> ?? entries.ToList();

        // 並びも項目も 1 つも変わっていないなら何もしない。差し替えると ListBox が
        // 全行のコンテナを作り直し、選択とスクロール位置も一度失われるため、フォルダー監視に
        // よる自動更新のたびに一覧が点滅して見える。MergeReloadedEntries が変化のない行を
        // 使い回すので、実際に何も変わっていない読み直しはここで止まる。
        if (IsSameEntrySequence(_entries, next))
        {
            return;
        }

        // 増減だけで済むなら、その行だけを Add / Remove で通知する。ListBox は残りの行の
        // コンテナを作り直さないので、スクロール位置・選択・進行中のクリックが保たれる。
        if (TryPatchEntries(next))
        {
            OnPropertyChanged(nameof(HasNoEntries));
            return;
        }

        var previousSelection = _selection.Count > 0
            ? new HashSet<string>(_selection.Select(e => e.FullPath), WindowsPathIdentity.Instance)
            : null;

        SelectedEntry = null;
        if (_selection.Count > 0)
        {
            SetSelection([]);
        }

        _entries.Reset(next);
        _entriesRevision++;
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
            var revision = _entriesRevision;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_isDetached && revision == _entriesRevision)
                {
                    SelectionRestoreRequested?.Invoke(this, restored);
                }
            });
        }
    }

    /// <summary>増減だけで現在の一覧を目的の並びへ持っていけるなら、その差分を適用して true を返す。
    /// 並べ替え（共通する行の前後関係が変わる）や差分が大きすぎる場合は、何も変更せず false を返して
    /// 呼び出し側の一括置換に任せる。</summary>
    private bool TryPatchEntries(List<FileSystemEntry> next)
    {
        // 初回表示（空 → 全件）は一括置換の方が安い
        if (_entries.Count == 0 || next.Count == 0)
        {
            return false;
        }

        var currentSet = new HashSet<FileSystemEntry>(_entries);
        var nextSet = new HashSet<FileSystemEntry>(next);
        var removed = _entries.Count(entry => !nextSet.Contains(entry));
        var added = next.Count(entry => !currentSet.Contains(entry));
        if (removed + added == 0 || removed + added > MaxPatchedEntryChanges)
        {
            return false;
        }

        // 両方に残る行の並び順が一致していなければ「増減」では表せない（＝並べ替え）
        var keptCurrent = _entries.Where(nextSet.Contains).ToList();
        var keptNext = next.Where(currentSet.Contains).ToList();
        if (keptCurrent.Count != keptNext.Count)
        {
            return false;
        }

        for (var i = 0; i < keptCurrent.Count; i++)
        {
            if (!ReferenceEquals(keptCurrent[i], keptNext[i]))
            {
                return false;
            }
        }

        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!nextSet.Contains(_entries[i]))
            {
                _entries.RemoveAt(i);
            }
        }

        for (var i = 0; i < next.Count; i++)
        {
            if (i >= _entries.Count || !ReferenceEquals(_entries[i], next[i]))
            {
                _entries.Insert(i, next[i]);
            }
        }

        return true;
    }

    /// <summary>増減で反映する上限。これを超えるなら一括置換（Reset 1 回）の方が速い。</summary>
    private const int MaxPatchedEntryChanges = 128;

    /// <summary>2 つの一覧が「同じ項目が同じ順に並んでいる」か。行の同一性はインスタンス参照で見る。
    /// MergeReloadedEntries が同じファイルの行を使い回すので、参照が違うのは増減・並べ替え・
    /// 作り直しが実際に起きたときだけになる。</summary>
    private static bool IsSameEntrySequence(IReadOnlyList<FileSystemEntry> current, IReadOnlyList<FileSystemEntry> next)
    {
        if (ReferenceEquals(current, next)) return true;
        if (current.Count != next.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], next[i])) return false;
        }

        return true;
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

    /// <summary>ギャラリーへ入る直前の表示モード。閉じたときはここへ戻す
    /// （利用者が選んでいた表示を、ギャラリーを覗いただけで失わせないため）。</summary>
    private ViewMode _viewModeBeforeGallery = ViewMode.LargeIcons;

    /// <summary>ギャラリー表示へ入る。抜ける先を覚えるため、ここが唯一の入口。</summary>
    public void EnterGallery()
    {
        if (!CanChangeViewMode || IsGalleryView)
        {
            return;
        }

        _viewModeBeforeGallery = ViewMode;
        ViewMode = ViewMode.Gallery;
    }

    /// <summary>ギャラリー表示を終える（Esc / ✕ / フィルムストリップ上の Ctrl+ホイール）。
    /// 入る前の表示モードへ戻す。フォルダー別記憶からギャラリーで復元された場合は
    /// 覚えている直前値が無いので、大アイコンへ戻す。</summary>
    public void LeaveGallery()
    {
        if (!IsGalleryView)
        {
            return;
        }

        ViewMode = _viewModeBeforeGallery == ViewMode.Gallery
            ? ViewMode.LargeIcons
            : _viewModeBeforeGallery;
    }

    /// <summary>
    /// ギャラリーで画像として扱う拡張子か。通常の画像に加えて、シェルのコーデック任せで
    /// 表示する新世代フォーマットと各社の RAW も含める。
    /// </summary>
    public static bool IsGalleryImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExtensions.Contains(ext) || IsShellDecodedImage(ext);
    }

    /// <summary>ギャラリーで再生できる動画か。</summary>
    public static bool IsGalleryVideo(string path)
        => VideoPlaybackSession.IsPlayable(Path.GetExtension(path));

    /// <summary>指定エントリを選んだ状態でギャラリー表示へ入る（全画面化は呼び出し側が行う）。</summary>
    public void OpenInGallery(FileSystemEntry entry)
    {
        SelectedEntry = entry;
        EnterGallery();
        // ViewMode の切り替えで選択が動くことはないが、フィルムストリップ側の初期選択を
        // 確実にこの 1 枚へ合わせるため、入ったあとにもう一度指定する。
        SelectedEntry = entry;
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

        input = ResolveTypedPath(input, CurrentPath);

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

    /// <summary>
    /// アドレスバーに打ち込まれた文字列を、実際に開くパスへ直す。
    ///
    /// <c>C:</c> のようなドライブ相対表記は、Windows では「ドライブ直下」ではなく
    /// 「そのプロセスにおける C: のカレントディレクトリ」を指す（実測: <c>Directory.Exists("C:")</c> は true、
    /// <c>new DirectoryInfo("C:").FullName</c> はカレントディレクトリ）。そのまま開くと、エクスプローラーが
    /// <c>C:\</c> を開くのに対して Kiriha は実行ファイルの場所を開いてしまい、しかも <c>CurrentPath</c> ・
    /// 監視キー・フォルダー別設定のキーに <c>C:</c> という相対表記が残る。
    ///
    /// 相対パスの基準はプロセスのカレントディレクトリ（＝インストール先）ではなく、
    /// いま表示しているフォルダーにする。エクスプローラーのアドレスバーと同じ解釈で、
    /// 利用者にとって意味のある基準はこちらしかないため。
    ///
    /// 解決できない文字列はそのまま返し、呼び出し側の「開けませんでした」に任せる。
    /// </summary>
    internal static string ResolveTypedPath(string input, string currentPath)
    {
        if (input.Length == 0)
        {
            return input;
        }

        // "C:" → "C:\"（ドライブ直下）
        if (input.Length == 2 && input[1] == ':' && char.IsAsciiLetter(input[0]))
        {
            return input + Path.DirectorySeparatorChar;
        }

        try
        {
            var full = Path.IsPathFullyQualified(currentPath)
                ? Path.GetFullPath(input, currentPath)
                : Path.GetFullPath(input);
            // 末尾の区切りは落とす（ルートは TrimEndingDirectorySeparator が保つ）。
            // 列挙が返す FullName と同じ形にしておかないと、同じフォルダーがタブ・監視・
            // フォルダー別設定で別物として扱われる余地が残る。
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex)
        {
            Logger.Log($"アドレスバーの入力を解決できませんでした: {input}: {ex.GetType().Name}", LogLevel.Warning);
            return input;
        }
    }

    /// <summary>
    /// アドレス入力を確定せずに終える（Esc / フォーカス喪失）。
    /// 通常表示は PathText をそのまま出すため、入力途中のテキストを現在地の表記へ戻す。
    /// </summary>
    public void CancelPathEditing()
    {
        IsEditingPath = false;
        PathText = CurrentPath == FileSystemService.ComputerPath ? "PC" : CurrentPath;
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
                ? grouped.ThenBy(e => e.SortName, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenByDescending(e => e.SortName, StringComparer.CurrentCultureIgnoreCase),
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
        DeletePermanentCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCopyPath));
        OnPropertyChanged(nameof(CanCopySelectedPath));

        _selectionSizeCts?.Cancel();
        _selectionSizeCts?.Dispose();
        _selectionSizeCts = null;

        if (selection.Count == 0)
        {
            SelectionText = "";
            SelectionModifiedText = "";
            return;
        }

        // 更新日時は 1 件だけ選んでいるときの情報なので、複数選択では出さない（サイズは合計を出す）
        SelectionModifiedText = selection.Count == 1 && selection[0].ModifiedText is { Length: > 0 } modified
            ? LocalizationService.Text("Text.Status.Modified", modified)
            : "";

        var fileSize = selection.Where(e => e.Size is not null).Sum(e => e.Size!.Value);
        var hasSize = selection.Any(e => e.Size is not null);
        var folders = selection.Count(e => e.IsDirectory);
        var files = selection.Count - folders;
        var breakdown = folders > 0 && files > 0 ? " " + LocalizationService.Text("Text.Status.Breakdown", folders, files) : "";

        // フォルダーは自身のサイズを持たないので、選択に含まれていれば中身を数えて合計に足す。
        // ドライブ一覧（PC）は 1 台まるごとの走査になってしまうので対象外。
        var folderPaths = CurrentPath == FileSystemService.ComputerPath
            ? []
            : selection.Where(e => e.IsDirectory).Select(e => e.FullPath).ToList();

        SelectionText = BuildSelectionText(selection.Count, fileSize, hasSize || folderPaths.Count > 0, breakdown, computing: folderPaths.Count > 0);

        if (_previewEnabled && selection.Count > 1)
        {
            PreviewBitmap?.Dispose();
            PreviewBitmap = null;
            PreviewText = "";
            PreviewInfo = SelectionText;
        }

        if (folderPaths.Count > 0)
        {
            var cts = new CancellationTokenSource();
            _selectionSizeCts = cts;
            _ = SumFolderSizesAsync(folderPaths, selection.Count, fileSize, breakdown, selection.Count > 1, cts.Token);
        }
    }

    /// <summary>選択中フォルダーの中身を数え終えるまで、途中経過を書き換えるための解除トークン。</summary>
    private CancellationTokenSource? _selectionSizeCts;

    /// <summary>ステータスバーの「n 個の項目を選択 …」を組み立てる。集計中はサイズの後ろに … を付ける。</summary>
    private static string BuildSelectionText(int count, long size, bool hasSize, string breakdown, bool computing)
    {
        var sizeText = hasSize ? $" {FileSystemEntry.FormatSize(size)}{(computing ? "…" : "")}" : "";
        return LocalizationService.Text("Text.Status.ItemsSelected", count) + sizeText + breakdown;
    }

    /// <summary>
    /// 選択されたフォルダーの中身を再帰的に足し合わせ、途中経過をステータスバーへ流す。
    ///
    /// 走査は当然ながら重い（数万ファイルのフォルダーなら秒単位）ので、UI スレッドでは絶対に行わず、
    /// 選択が変わったら即座に解除する。再解析ポイント（ジャンクション / シンボリックリンク）は
    /// たどらない ― 循環すると終わらなくなるうえ、エクスプローラーも実体の中身だけを数えるため。
    /// </summary>
    private async Task SumFolderSizesAsync(List<string> folders, int count, long fileSize, string breakdown, bool isMultiple, CancellationToken token)
    {
        await Task.Run(() =>
        {
            // AttributesToSkip はフォルダーだけでなくファイルにも効くため、ここで ReparsePoint を
            // 弾くと OneDrive のプレースホルダー（未ダウンロードのクラウドファイルは再解析ポイント）
            // まで合計から丸ごと落ちる。除きたいのは「リンク先へ潜ること」だけなので、
            // ファイルは数え、再帰の可否だけを ShouldRecursePredicate で止める。
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };

            var total = 0L;
            var nextReport = Environment.TickCount64 + SelectionSizeReportIntervalMs;
            foreach (var folder in folders)
            {
                try
                {
                    var sizes = new System.IO.Enumeration.FileSystemEnumerable<long>(
                        folder,
                        (ref SysEntry entry) => entry.Length,
                        options)
                    {
                        ShouldIncludePredicate = (ref SysEntry entry) => !entry.IsDirectory,
                        // ジャンクション / シンボリックリンクの先へは潜らない（循環すると終わらないため）
                        ShouldRecursePredicate = (ref SysEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                    };

                    foreach (var size in sizes)
                    {
                        if (token.IsCancellationRequested) { return; }
                        total += size;
                        if (Environment.TickCount64 >= nextReport)
                        {
                            nextReport = Environment.TickCount64 + SelectionSizeReportIntervalMs;
                            Post(total, computing: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 権限不足や途中で消えたフォルダーは、そこまでの合計で続ける
                    Logger.LogException($"フォルダーのサイズを集計できませんでした: {folder}", ex);
                }
            }

            Post(total, computing: false);

            void Post(long folderTotal, bool computing)
            {
                var text = BuildSelectionText(count, fileSize + folderTotal, hasSize: true, breakdown, computing);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isDetached || token.IsCancellationRequested) { return; }
                    SelectionText = text;
                    if (_previewEnabled && isMultiple)
                    {
                        PreviewInfo = text;
                    }
                });
            }
        }, CancellationToken.None);
    }

    /// <summary>フォルダー集計の途中経過をステータスバーへ出す間隔（ミリ秒）。</summary>
    private const int SelectionSizeReportIntervalMs = 300;

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

    /// <summary>
    /// 「パスをコピー」（Ctrl+Shift+C）が使えるか。選択があればその選択、無ければ現在のフォルダーを
    /// コピーする（エクスプローラーの Ctrl+Shift+C と同じ）ので、選択なしでも有効。ドライブ一覧の
    /// 「PC」はコピーできる実体のパスが無いので除く。コピー自体はクリップボードを持つ View 側
    /// （<c>MainWindow.CopySelectedPaths</c>）が行うため、コマンドではなく活性判定だけを公開する。
    /// </summary>
    public bool CanCopyPath => !IsSettingsTab && (HasSelection || CanCopyFolderPath);

    /// <summary>
    /// アドレスバー隣の「リンクをコピー」が使えるか。こちらは選択に関係なく現在のフォルダー
    /// そのもののパスをコピーするボタンなので、選択の有無では変化しない。
    /// </summary>
    public bool CanCopyFolderPath => !IsSettingsTab && !IsComputerRoot && CurrentPath.Length > 0;

    /// <summary>
    /// コマンドバーの「パスのコピー」が使えるか。アドレスバー隣のボタンと役割を分けるため、
    /// こちらは選択した項目専用でフォルダーへのフォールバックを持たない。
    /// </summary>
    public bool CanCopySelectedPath => !IsSettingsTab && HasSelection;

    /// <summary>
    /// ドライブは切り取り / コピー / 削除 / 名前変更の対象にしない（誤操作防止）。
    /// クリップボードへ載せた時点で、貼り付け先でドライブ中身の移動・コピーになり得るため、
    /// 削除だけでなくコピーも同じ判定で塞ぐ。
    /// </summary>
    private bool HasModifiableSelection => _selection.Count > 0 && _selection.All(e => !e.IsDrive);

    /// <summary>クリップボード・削除に渡してよい選択（ドライブを除いたもの）。</summary>
    private List<FileSystemEntry> ModifiableSelection => _selection.Where(e => !e.IsDrive).ToList();

    private bool HasSingleSelection => _selection.Count == 1 && !_selection[0].IsDrive;

    /// <summary>新規作成が可能か（PC ビューでは不可）。</summary>
    public bool CanCreateNew => CurrentPath != FileSystemService.ComputerPath && !IsSettingsTab;

    [RelayCommand(CanExecute = nameof(HasModifiableSelection))]
    private void Cut() => SetClipboard(cut: true);

    [RelayCommand(CanExecute = nameof(HasModifiableSelection))]
    private void Copy() => SetClipboard(cut: false);

    /// <summary>
    /// 選択をクリップボードへ載せる。RelayCommand.Execute は CanExecute を見ずに走るので、
    /// ドライブの除外は活性判定だけに任せずここでも行う。
    /// </summary>
    private void SetClipboard(bool cut)
    {
        var targets = ModifiableSelection;
        if (targets.Count == 0)
        {
            return;
        }

        if (ClipboardFileService.SetFiles(targets.Select(e => e.FullPath).ToList(), cut))
        {
            var message = LocalizationService.Text(
                cut ? "Text.Clipboard.Cut" : "Text.Clipboard.Copied", targets.Count);
            StatusText = message;
            // ステータスバーは非表示にもできるうえ視線から遠いので、結果はトーストでも返す。
            ToastRequested?.Invoke(this, new ToastRequest(
                LocalizationService.Text(cut ? "Text.Command.Cut" : "Text.Common.Copy"), message));
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

    [RelayCommand(CanExecute = nameof(HasModifiableSelection))]
    private Task DeleteAsync() => DeleteCoreAsync(permanent: false);

    /// <summary>Shift+Delete の完全削除（ごみ箱を経由しない、システム確認あり）。</summary>
    [RelayCommand(CanExecute = nameof(HasModifiableSelection))]
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
        // ドライブ（PC ビューの C: 等）は絶対に削除対象にしない。IFileOperation にドライブ直下を
        // 渡すと中身の一括削除が始まってしまうため、CanExecute（HasModifiableSelection）だけでなく
        // ここでも弾く。RelayCommand.Execute は CanExecute を見ずに走るので、保険が要る。
        var deletable = ModifiableSelection;
        if (deletable.Count == 0)
        {
            return;
        }

        var targets = deletable.Select(e => e.FullPath).ToList();
        // 削除後はエクスプローラーと同じく隣接項目を選択する
        var anchorIndex = _entries.IndexOf(deletable[0]);

        var recycled = new List<RecycledItem>();
        var result = await Task.Run(() => FileOperationService.DeleteToRecycleBin(targets, permanent, recycled));
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled) StatusText = LocalizationService.Text("Text.Op.DeleteFailed", FormatOpError(result.NativeErrorCode));
            return;
        }

        // Ctrl+Z で戻せるようにごみ箱側の項目を控える（完全削除では空のまま）
        FileUndoService.PushDelete(recycled);
        await NavigateToAsync(CurrentPath, record: false);

        if (anchorIndex >= 0 && Entries.Count > 0)
        {
            SelectedEntry = Entries[Math.Min(anchorIndex, Entries.Count - 1)];
        }
    }

    /// <summary>Ctrl+Z。直近の削除をごみ箱から元の場所へ戻す（エクスプローラーと同じ挙動）。
    /// 戻せる操作が無いときは何もしない（エクスプローラーもメッセージを出さない）。</summary>
    public async Task UndoLastOperationAsync()
    {
        if (FileUndoService.PopDelete() is not { } recycled)
        {
            return;
        }

        var result = await Task.Run(() => FileOperationService.RestoreFromRecycleBin(recycled));
        if (result.IsSuccess)
        {
            await NavigateToAsync(CurrentPath, record: false);
            StatusText = LocalizationService.Text("Text.Op.Undone", recycled.Count);
        }
        else if (result.IsCancelled)
        {
            // 利用者が確認ダイアログで「キャンセル」を選んだだけなので、項目はごみ箱に残っている。
            // 履歴を消してしまうと、もう一度 Ctrl+Z を押しても二度と戻せなくなる。
            FileUndoService.PushDelete(recycled);
        }
        else
        {
            // 戻せなかった分は履歴へ戻さない（ごみ箱を空にした後などは何度試しても失敗するため）
            StatusText = LocalizationService.Text("Text.Op.UndoFailed", FormatOpError(result.NativeErrorCode));
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
    /// <returns>
    /// 実際に改名できたときは新しいパス、そうでなければ null。
    /// 呼び出し側はこれを見てお気に入りのパスを追従させる（お気に入りは名前を持たず実体名を出すため）。
    /// </returns>
    public async Task<string?> CommitRenameAsync(FileSystemEntry entry, string newName)
    {
        // OK でダイアログを閉じた時点で新規作成の保留状態は終了する。
        // 入力不備で改名できなかった場合も、後の通常リネームのキャンセルで削除されないようにする。
        _pendingNewFolderPaths.Remove(entry.FullPath);

        newName = newName.Trim();
        if (newName.Length == 0 || newName == entry.Name)
        {
            return null;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText = LocalizationService.Text("Text.Rename.InvalidChars");
            return null;
        }

        var dir = Path.GetDirectoryName(entry.FullPath);
        if (dir is null)
        {
            return null;
        }

        var newPath = Path.Combine(dir, newName);
        var isCaseOnlyRename = string.Equals(entry.FullPath, newPath, StringComparison.OrdinalIgnoreCase);
        if (!isCaseOnlyRename && (File.Exists(newPath) || Directory.Exists(newPath)))
        {
            StatusText = LocalizationService.Text("Text.Rename.AlreadyExists", newName);
            return null;
        }

        if (isCaseOnlyRename)
        {
            // Windows の大文字・小文字だけの変更は同一パス扱いになるため、一時名を経由する。
            var temporary = Path.Combine(dir, $".kiriha-rename-{Guid.NewGuid():N}");
            // コピー・削除と同じ理由で背景スレッドへ出す。IFileOperation は同期ブロッキングで、
            // FileOperationService.Execute は専用 STA スレッドを Join するため、UI スレッドから
            // 直接呼ぶと確認ダイアログや低速パスの間ウィンドウ全体が固まる。
            var first = await Task.Run(() => FileOperationService.Rename(entry.FullPath, temporary));
            if (!first.IsSuccess)
            {
                if (!first.IsCancelled) StatusText = LocalizationService.Text("Text.Op.RenameFailed", FormatOpError(first.NativeErrorCode));
                return null;
            }

            // 一時名への改名は既に成功しているため、ここで失敗したら一時名のまま残ってしまう。必ず元へ戻す。
            var second = await Task.Run(() => FileOperationService.Rename(temporary, newPath));
            if (!second.IsSuccess)
            {
                var rollback = await Task.Run(() => FileOperationService.Rename(temporary, entry.FullPath));
                StatusText = rollback.IsSuccess
                    ? LocalizationService.Text("Text.Rename.RevertedToOriginal")
                    : LocalizationService.Text("Text.Rename.StuckAsTemporary", Path.GetFileName(temporary));
                await NavigateToAsync(CurrentPath, record: false);
                return null;
            }
        }
        else
        {
            var result = await Task.Run(() => FileOperationService.Rename(entry.FullPath, newPath));
            if (!result.IsSuccess)
            {
                if (!result.IsCancelled) StatusText = LocalizationService.Text("Text.Op.RenameFailed", FormatOpError(result.NativeErrorCode));
                return null;
            }
        }
        await NavigateToAsync(CurrentPath, record: false);

        // 変更後の項目を選択し直す
        var renamed = Entries.FirstOrDefault(e => string.Equals(e.FullPath, newPath, StringComparison.OrdinalIgnoreCase));
        if (renamed is not null)
        {
            SelectedEntry = renamed;
        }

        return newPath;
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

    /// <summary>右ボタンドラッグの「ショートカットをここに作成」。ドロップされたパスごとに .lnk を作る。</summary>
    public async Task DropShortcutsAsync(IReadOnlyList<string> files, string destDir)
    {
        if (files.Count == 0 || destDir.Length == 0)
        {
            return;
        }

        var result = await Task.Run(() => ShellLinkService.Create(files, destDir));
        if (result.IsSuccess)
        {
            Refresh();
            StatusText = LocalizationService.Text("Text.Op.ShortcutsCreated", files.Count);
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

    /// <summary>「PC」と設定タブにはカレントディレクトリが無いのでターミナルを開けない。</summary>
    public bool CanOpenTerminal => !IsSettingsTab && !IsComputerRoot && CurrentPath.Length > 0;

    /// <summary>
    /// ターミナルを開くコマンドの表示名。設定「ターミナルを管理者として開く」が ON なら
    /// 昇格することが分かる文言に差し替える（UAC が出る操作なので、押す前に見えている必要がある）。
    /// </summary>
    public string OpenTerminalText => LocalizationService.Text(
        TerminalLauncher.RunAsAdmin ? "Text.Common.OpenInTerminalAsAdmin" : "Text.Common.OpenInTerminal");

    /// <summary>設定「ターミナルを管理者として開く」が切り替わったときに表示名を取り直す。</summary>
    public void NotifyTerminalOptionChanged() => OnPropertyChanged(nameof(OpenTerminalText));

    /// <summary>ターミナル（Windows Terminal、無ければ cmd）を現在のフォルダーで開く。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenTerminal))]
    private void OpenTerminal() => OpenTerminalAt(CurrentPath);

    /// <summary>指定フォルダーをターミナルで開く。
    /// コンテキストメニューの「ターミナルで開く」も、選択したフォルダーを渡してここを通る。</summary>
    public void OpenTerminalAt(string path)
    {
        if (TerminalLauncher.TryOpen(path) is { } error)
        {
            StatusText = LocalizationService.Text("Text.Launch.TerminalFailed", error);
        }
    }

    /// <summary>現在のフォルダーをエクスプローラーで開く。</summary>
    [RelayCommand]
    private void OpenInExplorer()
        => OpenFolderInExplorer(CurrentPath == FileSystemService.ComputerPath ? null : CurrentPath);

    /// <summary>指定フォルダー（null なら PC）をエクスプローラーで開く。
    /// コンテキストメニューの「エクスプローラーで開く」もここを通る。</summary>
    public void OpenFolderInExplorer(string? path)
    {
        try
        {
            TrustedProcessLauncher.Start(
                "explorer.exe",
                path is null ? [] : [path],
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

    /// <summary>
    /// 一つ上の階層へ移動する。移動後は「いま出てきたフォルダー」を選択し、一覧の中央へスクロールする
    /// （エクスプローラーと同じで、どこから上がってきたのかを見失わないため）。
    /// </summary>
    [RelayCommand]
    private async Task GoUpAsync()
    {
        if (CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        var from = CurrentPath;
        var parent = Directory.GetParent(CurrentPath);
        await NavigateToAsync(parent?.FullName ?? FileSystemService.ComputerPath);
        RevealEntry(from, center: true);
    }

    /// <summary>指定パスの行を選択し、View へスクロール（表示）を依頼する。
    /// 固定タブが移動を別タブへ委譲した場合など、一覧に無いパスは何もしない。</summary>
    private void RevealEntry(string path, bool center)
    {
        if (_isDetached || _entryByPath.GetValueOrDefault(path) is not { } entry || !_entries.Contains(entry))
        {
            return;
        }

        SelectedEntry = entry;
        RevealEntryRequested?.Invoke(this, new RevealRequest(entry, center));
    }

    // ===== 先頭一致ジャンプ（一覧で文字を打つと該当ファイルへ移動する。エクスプローラーと同じ） =====

    /// <summary>打ち込み途中の文字列。最後の入力から一定時間が空くと捨てる。</summary>
    private string _typeAheadPrefix = "";
    private DateTime _typeAheadAtUtc;

    /// <summary>この間隔が空いたら打ち直しとみなす（エクスプローラーの体感に合わせた 1 秒）。</summary>
    private static readonly TimeSpan TypeAheadResetDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 一覧へ文字が入力されたときの移動先を決めて選択する。移動したら true。
    /// 「S → O」と続けて打てば SoftwareDistribution のように、打った文字列の先頭一致で移動する。
    /// </summary>
    public bool TypeAheadSelect(string text)
    {
        if (IsSettingsTab || text.Length == 0 || char.IsControl(text[0]))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var expired = now - _typeAheadAtUtc > TypeAheadResetDelay;
        // 先頭の空白は打ち始めとして意味を持たない（一覧では選択トグルのキーでもある）。
        if (text[0] == ' ' && (expired || _typeAheadPrefix.Length == 0))
        {
            return false;
        }

        _typeAheadPrefix = expired ? text : _typeAheadPrefix + text;
        _typeAheadAtUtc = now;

        var index = FindTypeAheadIndex(_entries, _typeAheadPrefix, _entries.IndexOf(SelectedEntry!));
        if (index < 0)
        {
            return false;
        }

        var entry = _entries[index];
        SelectedEntry = entry;
        // 中央寄せはしない。エクスプローラーと同じく、見えていれば動かさず最小限だけスクロールする。
        RevealEntryRequested?.Invoke(this, new RevealRequest(entry, Center: false));
        return true;
    }

    /// <summary>
    /// 先頭一致ジャンプの移動先を求める（該当なしは -1）。エクスプローラーと同じ 2 つの規則に従う。
    /// 1 文字だけのとき、および同じ文字を続けて打ったときは「次の候補へ送る」ので現在位置の次から探す。
    /// 2 文字以上の打ち込みは現在位置を含めて探す（打ち足して絞り込む操作なので、今の行が該当なら留まる）。
    /// どちらも末尾まで行ったら先頭へ回り込む。
    /// </summary>
    internal static int FindTypeAheadIndex(IReadOnlyList<FileSystemEntry> entries, string prefix, int currentIndex)
    {
        if (entries.Count == 0 || prefix.Length == 0)
        {
            return -1;
        }

        var repeating = prefix.Length > 1 && prefix.All(c => c == prefix[0]);
        var needle = repeating ? prefix[..1] : prefix;
        var advance = repeating || prefix.Length == 1;
        var start = Math.Max(0, (advance ? currentIndex + 1 : currentIndex) % entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var index = (start + i) % entries.Count;
            if (entries[index].DisplayName.StartsWith(needle, StringComparison.CurrentCultureIgnoreCase))
            {
                return index;
            }
        }

        return -1;
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
        if (!CanChangeViewMode || !Enum.TryParse<ViewMode>(mode, out var value))
        {
            return;
        }

        // ギャラリーへ入るときだけは、抜ける先を覚えるため専用の入口を通す。
        if (value == ViewMode.Gallery)
        {
            EnterGallery();
            return;
        }

        ViewMode = value;
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
