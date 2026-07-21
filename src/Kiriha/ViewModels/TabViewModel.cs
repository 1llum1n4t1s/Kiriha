using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiriha.Models;
using Kiriha.Services;

namespace Kiriha.ViewModels;

/// <summary>1 タブ分の状態（現在パス・エントリ一覧・履歴・表示モード・固定状態）を持つ ViewModel。</summary>
public partial class TabViewModel : ObservableObject
{
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly ShellOptions _options;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _filterDebounceCts;
    private bool _isDetached;
    private bool _suppressSearchFilter;
    private long _searchGeneration;
    private long _navigationGeneration;

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
        nameof(ListOrientation),
        nameof(IsViewExtraLarge), nameof(IsViewLarge), nameof(IsViewMedium),
        nameof(IsViewSmall), nameof(IsViewList), nameof(IsViewDetails))]
    private ViewMode _viewMode = ViewMode.Details;

    /// <summary>タブ自身（✕ ボタン / コンテキストメニュー）からの閉じる要求。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>固定タブから別階層へ移動しようとしたとき、新しい通常タブで開く要求。</summary>
    public event Action<string>? PinnedNavigationRequested;

    /// <summary>名前の変更 UI の表示要求（View 側でダイアログを出す）。</summary>
    public event EventHandler<FileSystemEntry>? RenameRequested;

    private List<FileSystemEntry> _selection = new();
    private readonly HashSet<string> _pendingNewFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DetailColumnViewModel> DetailColumns { get; }

    /// <summary>現在の複数選択（切り取り / コピー / 削除の対象）。</summary>
    public IReadOnlyList<FileSystemEntry> Selection => _selection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsSortByName), nameof(IsSortByModified), nameof(IsSortByCreated), nameof(IsSortByType), nameof(IsSortBySize),
        nameof(NameSortGlyph), nameof(ModifiedSortGlyph), nameof(TypeSortGlyph), nameof(SizeSortGlyph), nameof(CreatedSortGlyph))]
    private string _sortKey = "Name";

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
    public string SearchPlaceholder => $"{Title}の検索";

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(SearchPlaceholder));

    /// <summary>ステータスバー右側（空き領域）。</summary>
    [ObservableProperty]
    private string _freeSpaceText = "";

    /// <summary>コンパクトビュー（行の高さを詰める）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowHeight), nameof(ListRowHeight))]
    private bool _isCompactView;

    public double RowHeight => IsCompactView ? 24 : 30;

    /// <summary>Windows エクスプローラーの一覧表示に合わせた行高。</summary>
    public double ListRowHeight => IsCompactView ? 18 : 20;

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

    private static readonly string[] VideoThumbnailExtensions =
        [".mp4", ".m4v", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg", ".mts", ".m2ts"];

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
        if (_previewEnabled)
        {
            UpdatePreview();
        }
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        PreviewBitmap?.Dispose();
        PreviewBitmap = null;
        PreviewText = "";
        PreviewInfo = "";
    }

    private async void UpdatePreview()
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        var entry = SelectedEntry;
        if (entry is null || entry.IsDirectory)
        {
            PreviewBitmap?.Dispose();
            PreviewBitmap = null;
            PreviewText = "";
            if (entry is null)
            {
                PreviewInfo = _selection.Count > 1
                    ? $"{_selection.Count} 個の項目を選択"
                    : "";
            }
            else
            {
                PreviewInfo = $"{entry.Name}\nファイル フォルダー";
                _ = LoadFolderPreviewInfoAsync(entry, cts.Token);
            }

            return;
        }

        PreviewInfo = $"{entry.Name}\n{entry.TypeText}  {entry.SizeText}\n更新日時: {entry.ModifiedText}";
        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();

        try
        {
            if (ImageExtensions.Contains(ext) && entry.Size is < 64 * 1024 * 1024)
            {
                var bmp = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(entry.FullPath);
                    return Bitmap.DecodeToWidth(stream, 480);
                }, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    PreviewBitmap?.Dispose();
                    PreviewBitmap = bmp;
                    PreviewText = "";
                    // 画像は寸法も表示する
                    PreviewInfo = $"{entry.Name}\n{entry.TypeText}  {entry.SizeText}  {bmp.PixelSize.Width}×{bmp.PixelSize.Height}\n更新日時: {entry.ModifiedText}";
                }
                else
                {
                    bmp.Dispose();
                }

                return;
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
        catch
        {
            // プレビュー失敗は情報表示のみにフォールバック
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
                PreviewInfo = $"{entry.Name}\nファイル フォルダー\n{count}{(capped ? "+" : "")} 個のファイル  {FileSystemEntry.FormatSize(size)}{(capped ? " 以上" : "")}";
            }
        }
        catch
        {
            // 計算失敗時は基本情報のまま
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

    private void OnObservedDirectoryChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDetached && SearchText.Length == 0)
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

        _searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _searchCts = cts;
        var generation = Interlocked.Increment(ref _searchGeneration);
        StatusText = "検索中... (サブフォルダーを含む)";

        var root = CurrentPath;
        var showExt = _options.ShowExtensions;
        var showHidden = _options.ShowHidden;
        var useMaterialIcons = _options.UseMaterialIcons;
        var preferLight = useMaterialIcons && MaterialIconService.IsLightTheme();
        var results = await Task.Run(() =>
        {
            var list = new List<FileSystemEntry>();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = showHidden ? FileAttributes.System : FileAttributes.Hidden | FileAttributes.System,
            };
            foreach (var item in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
            {
                if (cts.IsCancellationRequested || list.Count >= 1000)
                {
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

            return list;
        }, cts.Token);

        if (cts.IsCancellationRequested || _isDetached || generation != _searchGeneration
            || !string.Equals(root, CurrentPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(query, SearchText.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        ReplaceEntries(ApplySort(results));

        StatusText = $"{results.Count} 件見つかりました (サブフォルダー含む{(results.Count >= 1000 ? "、上限 1000 件" : "")})";
    }

    // ===== アイコンビューの画像・動画・PDFサムネイル =====

    private CancellationTokenSource? _thumbnailCts;
    // 最大160論理pxを200% DPIでも等倍表示できる解像度。画質と一覧のメモリ使用量を両立する。
    private const int ThumbnailPixelSize = 320;
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _loadingThumbnails = new(StringComparer.OrdinalIgnoreCase);

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
            _thumbnailCts?.Cancel();
            _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        }
        else
        {
            _thumbnailCts?.Cancel();
        }
    }

    public async Task EnsureThumbnailAsync(FileSystemEntry entry)
    {
        var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
        var isImage = ImageExtensions.Contains(extension);
        var isPdf = extension == ".pdf";
        var useShellThumbnail = VideoThumbnailExtensions.Contains(extension)
            || RawThumbnailExtensions.Contains(extension);
        if (!IsIconsView || _isDetached || entry.IsDirectory || entry.Thumbnail is not null
            || (!isImage && !isPdf && !useShellThumbnail)
            || (isImage && entry.Size is null or > 32 * 1024 * 1024)
            || !_loadingThumbnails.TryAdd(entry.FullPath, 0))
        {
            return;
        }

        var token = _thumbnailCts?.Token ?? _lifetimeCts.Token;
        try
        {
            await _thumbnailGate.WaitAsync(token);
            try
            {
                var bmp = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (isPdf)
                    {
                        return PdfThumbnailService.TryGetThumbnail(entry.FullPath, ThumbnailPixelSize);
                    }

                    if (useShellThumbnail)
                    {
                        return ShellThumbnailService.TryGetThumbnail(entry.FullPath, ThumbnailPixelSize);
                    }

                    using var stream = File.OpenRead(entry.FullPath);
                    return Bitmap.DecodeToWidth(stream, ThumbnailPixelSize);
                }, token);
                if (bmp is not null
                    && !token.IsCancellationRequested
                    && IsIconsView
                    && _allEntries.Contains(entry))
                {
                    entry.Thumbnail = bmp;
                }
                else
                {
                    bmp?.Dispose();
                }
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogException($"サムネイルを読み込めませんでした: {entry.FullPath}", ex);
        }
        finally
        {
            _loadingThumbnails.TryRemove(entry.FullPath, out _);
        }
    }

    [RelayCommand]
    private void ToggleCompactView() => IsCompactView = !IsCompactView;

    /// <summary>フィルタ前の全エントリ（検索の母集合）。</summary>
    private List<FileSystemEntry> _allEntries = new();

    public bool HasNoEntries => Entries.Count == 0;

    private string SortGlyph(string key)
        => SortKey == key ? (SortAscendingFlag ? "\uE70E" : "\uE70D") : "";

    public string NameSortGlyph => SortGlyph("Name");
    public string ModifiedSortGlyph => SortGlyph("Modified");
    public string TypeSortGlyph => SortGlyph("Type");
    public string SizeSortGlyph => SortGlyph("Size");
    public string CreatedSortGlyph => SortGlyph("Created");

    public bool IsSortByName => SortKey == "Name";
    public bool IsSortByModified => SortKey == "Modified";
    public bool IsSortByCreated => SortKey == "Created";
    public bool IsSortByType => SortKey == "Type";
    public bool IsSortBySize => SortKey == "Size";
    public bool IsSortAscending => SortAscendingFlag;
    public bool IsSortDescending => !SortAscendingFlag;

    /// <summary>現在表示中のパス。FileSystemService.ComputerPath ならドライブ一覧。</summary>
    public string CurrentPath { get; private set; } = FileSystemService.ComputerPath;

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
    [NotifyPropertyChangedFor(nameof(IconFontSize), nameof(IconItemWidth), nameof(IconCellWidth), nameof(IconCellHeight))]
    private double _iconSize = 28;

    public double IconFontSize => IconSize;

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
    {
        _options = options;
        DetailColumns =
        [
            new(this, "Name", "名前"),
            new(this, "Modified", "更新日時"),
            new(this, "Created", "作成日時"),
            new(this, "Type", "種類"),
            new(this, "Size", "サイズ"),
        ];
        IsSettingsTab = isSettingsTab;
        _options.Changed += OnOptionsChanged;
        ClipboardFileService.CutStateChanged += OnCutStateChanged;

        if (isSettingsTab)
        {
            Title = "設定";
            PathText = "設定";
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
        _watcherSubscription?.Dispose();
        _watcherSubscription = null;
        _filterDebounceCts?.Cancel();
        _searchCts?.Cancel();
        _thumbnailCts?.Cancel();
        foreach (var column in DetailColumns) column.Detach();
        DisposeEntryThumbnails(_allEntries);
        ClearPreview();
    }

    private void OnOptionsChanged(object? sender, ShellOptionChangedEventArgs e)
    {
        if (e.Kind == ShellOptionKind.UseMaterialIcons)
        {
            var enabled = _options.UseMaterialIcons;
            var preferLight = enabled && MaterialIconService.IsLightTheme();
            foreach (var entry in _allEntries.Concat(Entries).Distinct())
            {
                entry.UpdateMaterialIconKey(enabled, preferLight);
            }

            OnPropertyChanged(nameof(ShowMaterialIcons));
            OnPropertyChanged(nameof(UseEmojiIcons));
            return;
        }

        OnPropertyChanged(nameof(ShowHidden));
        OnPropertyChanged(nameof(ShowExtensions));
        OnPropertyChanged(nameof(ShowCheckBoxes));
        OnPropertyChanged(nameof(ShowMaterialIcons));
        OnPropertyChanged(nameof(UseEmojiIcons));
        if (e.Kind != ShellOptionKind.ShowCheckBoxes)
        {
            Refresh();
        }
    }

    /// <summary>切り取り状態の変化を全エントリの半透明表示へ反映する（エクスプローラーと同じ見た目）。</summary>
    private void OnCutStateChanged(object? sender, EventArgs e)
    {
        foreach (var entry in _allEntries)
        {
            entry.IsCut = ClipboardFileService.IsCutPath(entry.FullPath);
        }
    }

    public bool ShowCheckBoxes => _options.ShowCheckBoxes;

    /// <summary>絵文字の代わりに Material Icon Theme のアイコンを表示するか。</summary>
    public bool ShowMaterialIcons => _options.UseMaterialIcons;

    /// <summary>ShowMaterialIcons の反転（XAML の IsVisible バインディング用）。</summary>
    public bool UseEmojiIcons => !ShowMaterialIcons;

    /// <summary>指定パスへ移動する。失敗時は現状維持でステータスにエラーを出す。</summary>
    public void NavigateTo(string path, bool record = true)
        => _ = NavigateToAsync(path, record);

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
        // 同一パスの再読み込み（更新）ではスクロール / 選択を維持する
        var preserveSelection = WindowsPathIdentity.Instance.Equals(path, CurrentPath) ? SelectedEntry?.FullPath : null;

        List<FileSystemEntry> entries;
        try
        {
            entries = await Task.Run(() => FileSystemService.GetEntries(path, _options), _lifetimeCts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Logger.LogException($"フォルダーを開けませんでした: {path}", ex);
            StatusText = $"開けませんでした: {ex.Message}";
            PathText = CurrentPath;
            return;
        }

        if (_isDetached || generation != _navigationGeneration)
        {
            DisposeEntryThumbnails(entries);
            return;
        }

        if (record && !WindowsPathIdentity.Instance.Equals(CurrentPath, path))
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }

        CurrentPath = path;
        IsEditingPath = false;
        PathText = path == FileSystemService.ComputerPath ? "PC" : path;
        Title = path == FileSystemService.ComputerPath
            ? "PC"
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : path;

        DisposeEntryThumbnails(_allEntries);
        _allEntries = ApplySort(entries).ToList();
        // 移動で検索と種別フィルターをリセット（エクスプローラーと同じ）。プロパティ経由だと OnSearchTextChanged
        // → ApplyFilter が二重に走るだけで害はないため素直にプロパティへ代入する。
        _extensionFilter = null;
        _extensionFilterName = "";
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

        if (IsIconsView && _thumbnailCts is null)
            _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        Interlocked.Increment(ref _searchGeneration);
        if (_suppressSearchFilter || _isDetached) return;

        _filterDebounceCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _filterDebounceCts = cts;
        _ = ApplyFilterDebouncedAsync(cts.Token);
    }

    private async Task ApplyFilterDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            if (!token.IsCancellationRequested && !_isDetached)
            {
                ApplyFilter();
            }
        }
        catch (OperationCanceledException)
        {
            // 次の入力が来た場合は最後の検索語だけを反映する。
        }
    }

    private static void DisposeEntryThumbnails(IEnumerable<FileSystemEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.DisposeThumbnail();
        }
    }

    private HashSet<string>? _extensionFilter;
    private string _extensionFilterName = "";

    /// <summary>コマンドパレットから複数拡張子をまとめて絞り込む。</summary>
    public void ApplyExtensionFilter(string extensions, string displayName)
    {
        _extensionFilter = extensions.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _extensionFilterName = displayName;
        ApplyFilter();
    }

    public void ClearExtensionFilter()
    {
        _extensionFilter = null;
        _extensionFilterName = "";
        ApplyFilter();
    }

    /// <summary>検索テキストで現在のフォルダー内容を絞り込む。</summary>
    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<FileSystemEntry> filteredQuery = _allEntries;
        if (_extensionFilter is not null)
        {
            filteredQuery = filteredQuery.Where(e => !e.IsDirectory && _extensionFilter.Contains(Path.GetExtension(e.Name)));
        }
        if (query.Length > 0)
        {
            filteredQuery = filteredQuery.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        var filtered = filteredQuery.ToList();

        ReplaceEntries(filtered);

        var conditions = new List<string>();
        if (_extensionFilter is not null) conditions.Add($"種類: {_extensionFilterName}");
        if (query.Length > 0) conditions.Add($"検索: {query}");
        StatusText = conditions.Count == 0
            ? $"{filtered.Count} 個の項目"
            : $"{filtered.Count} 個の項目 ({string.Join(" / ", conditions)})";
    }

    /// <summary>一覧をスナップショット単位で置換し、3 つの ListBox へ各 1 回だけ通知する。</summary>
    private void ReplaceEntries(IEnumerable<FileSystemEntry> entries)
    {
        SelectedEntry = null;
        if (_selection.Count > 0)
        {
            SetSelection([]);
        }

        _entries = entries as List<FileSystemEntry> ?? entries.ToList();
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(HasNoEntries));
    }

    private void UpdateFreeSpace(string path)
    {
        try
        {
            if (path != FileSystemService.ComputerPath && Path.GetPathRoot(path) is { Length: > 0 } root)
            {
                var drive = new DriveInfo(root);
                FreeSpaceText = $"空き領域: {FileSystemEntry.FormatSize(drive.AvailableFreeSpace)}";
                return;
            }
        }
        catch
        {
            // ネットワークドライブ切断などは表示なしで続行
        }

        FreeSpaceText = "";
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
        _allEntries = ApplySort(_allEntries).ToList();
        var sortedVisibleEntries = ApplySort(Entries.ToList()).ToList();
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

        string rootLabel;
        try
        {
            rootLabel = FileSystemService.GetDriveLabel(new DriveInfo(root));
        }
        catch
        {
            rootLabel = root.TrimEnd(Path.DirectorySeparatorChar);
        }

        Breadcrumbs.Add(new BreadcrumbSegment { Name = rootLabel, Path = root });

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
            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"起動できませんでした: {ex.Message}";
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
                Process.Start(new ProcessStartInfo(input) { UseShellExecute = true });
                PathText = CurrentPath == FileSystemService.ComputerPath ? "PC" : CurrentPath;
            }
            catch (Exception ex)
            {
                StatusText = $"起動できませんでした: {ex.Message}";
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
            "Modified" => SortAscendingFlag
                ? grouped.ThenBy(e => e.Modified)
                : grouped.ThenByDescending(e => e.Modified),
            "Created" => SortAscendingFlag
                ? grouped.ThenBy(e => e.Created)
                : grouped.ThenByDescending(e => e.Created),
            "Type" => SortAscendingFlag
                ? grouped.ThenBy(e => e.TypeText, StringComparer.CurrentCultureIgnoreCase)
                : grouped.ThenByDescending(e => e.TypeText, StringComparer.CurrentCultureIgnoreCase),
            "Size" => SortAscendingFlag
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
        var breakdown = folders > 0 && files > 0 ? $" (フォルダー {folders}、ファイル {files})" : "";
        SelectionText = $"{selection.Count} 個の項目を選択{sizeText}{breakdown}";

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
            case "Modified":
                ShowColModified = !ShowColModified;
                break;
            case "Type":
                ShowColType = !ShowColType;
                break;
            case "Size":
                ShowColSize = !ShowColSize;
                break;
            case "Created":
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
            StatusText = $"{_selection.Count} 個の項目を切り取りました";
            PasteCommand.NotifyCanExecuteChanged();
        }
        else
        {
            StatusText = "クリップボードへ書き込めませんでした";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Copy()
    {
        if (ClipboardFileService.SetFiles(_selection.Select(e => e.FullPath).ToList(), cut: false))
        {
            StatusText = $"{_selection.Count} 個の項目をコピーしました";
            PasteCommand.NotifyCanExecuteChanged();
        }
        else
        {
            StatusText = "クリップボードへ書き込めませんでした";
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
            if (!ShellContextMenuService.InvokeDirectoryBackgroundVerb(0, dest, "paste"))
            {
                StatusText = "仮想ファイルを貼り付けられませんでした";
                return;
            }

            // Shell の貼り付けは非同期で完了することがある。初回更新を補助し、以後は
            // DirectoryObservationService の変更通知で一覧を追従させる。
            await Task.Delay(500);
            Refresh();
            return;
        }

        // 同一フォルダーへのコピー貼り付けはエクスプローラーと同じく自動リネーム（"- コピー"）
        var sameDir = !isCut && files.Any(f => WindowsPathIdentity.Instance.Equals(
            Path.GetDirectoryName(f), dest));

        // SHFileOperation は同期ブロッキングだが独自の進捗ダイアログを出すため背景スレッドで実行
        var result = await Task.Run(() => FileOperationService.CopyOrMove(files, dest, move: isCut, renameOnCollision: sameDir));
        if (result.IsBusy) { StatusText = "別のファイル操作が完了するまでお待ちください"; return; }
        if (result.IsSuccess && isCut) ClipboardFileService.Clear();
        if (result.IsSuccess) Refresh();
        else if (!result.IsCancelled) StatusText = $"貼り付けに失敗しました（エラー {result.NativeErrorCode}）";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeleteAsync() => DeleteCoreAsync(permanent: false);

    /// <summary>Shift+Delete の完全削除（ごみ箱を経由しない、システム確認あり）。</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DeletePermanent() => DeleteCoreAsync(permanent: true);

    private async Task DeleteCoreAsync(bool permanent)
    {
        var targets = _selection.Select(e => e.FullPath).ToList();
        // 削除後はエクスプローラーと同じく隣接項目を選択する
        var anchorIndex = _selection.Count > 0 ? _entries.IndexOf(_selection[0]) : -1;

        var result = await Task.Run(() => FileOperationService.DeleteToRecycleBin(targets, permanent));
        if (!result.IsSuccess)
        {
            if (result.IsBusy) StatusText = "別のファイル操作が完了するまでお待ちください";
            else if (!result.IsCancelled) StatusText = $"削除に失敗しました（エラー {result.NativeErrorCode}）";
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
            StatusText = "ファイル名に使えない文字が含まれています: \\ / : * ? \" < > |";
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
            StatusText = $"同じ名前のファイルまたはフォルダーが既に存在します: {newName}";
            return;
        }

        if (isCaseOnlyRename)
        {
            // Windows の大文字・小文字だけの変更は同一パス扱いになるため、一時名を経由する。
            var temporary = Path.Combine(dir, $".kiriha-rename-{Guid.NewGuid():N}");
            var first = FileOperationService.Rename(entry.FullPath, temporary);
            if (!first.IsSuccess)
            {
                if (first.IsBusy) StatusText = "別のファイル操作が完了するまでお待ちください";
                else if (!first.IsCancelled) StatusText = $"名前を変更できませんでした（エラー {first.NativeErrorCode}）";
                return;
            }

            var second = FileOperationService.Rename(temporary, newPath);
            if (!second.IsSuccess)
            {
                if (second.IsBusy)
                {
                    // 一時名への改名は既に成功しているため、ここで諦めると一時名のまま残ってしまう。
                    // ゲートが空くまで少し待って retry する。
                    await Task.Delay(300);
                    second = FileOperationService.Rename(temporary, newPath);
                }

                if (!second.IsSuccess)
                {
                    var rollback = FileOperationService.Rename(temporary, entry.FullPath);
                    StatusText = rollback.IsSuccess
                        ? "名前を変更できなかったため、元の名前へ戻しました"
                        : $"名前を変更できませんでした。一時名のまま残っています: {Path.GetFileName(temporary)}";
                    await NavigateToAsync(CurrentPath, record: false);
                    return;
                }
            }
        }
        else
        {
            var result = FileOperationService.Rename(entry.FullPath, newPath);
            if (!result.IsSuccess)
            {
                if (result.IsBusy) StatusText = "別のファイル操作が完了するまでお待ちください";
                else if (!result.IsCancelled) StatusText = $"名前を変更できませんでした（エラー {result.NativeErrorCode}）";
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
        // Windows の共有シート起動 API (IDataTransferManagerInterop) は WinRT 依存のため未対応
        StatusText = "共有は現在サポートされていません";
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
            .Where(f => !string.Equals(Path.GetDirectoryName(f)?.TrimEnd(Path.DirectorySeparatorChar),
                destDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (effective.Count == 0)
        {
            return;
        }

        var result = await Task.Run(() => FileOperationService.CopyOrMove(effective, destDir, move));
        if (result.IsSuccess)
        {
            Refresh();
            StatusText = $"{effective.Count} 個の項目を{(move ? "移動" : "コピー")}しました";
        }
        else if (result.IsBusy)
        {
            StatusText = "別のファイル操作が完了するまでお待ちください";
        }
        else if (!result.IsCancelled)
        {
            StatusText = $"ファイル操作に失敗しました（エラー {result.NativeErrorCode}）";
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
            var path = GetUniquePath("新しいフォルダー", "");
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
            StatusText = $"作成できませんでした: {ex.Message}";
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
            StatusText = $"新規フォルダーの作成を取り消せませんでした: {ex.Message}";
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
                StatusText = $"ターミナルを起動できませんでした: {ex.Message}";
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
            StatusText = $"エクスプローラーを起動できませんでした: {ex.Message}";
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
            var path = GetUniquePath($"新規 {template.DisplayName}", template.Extension);
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
            StatusText = $"作成できませんでした: {ex.Message}";
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
