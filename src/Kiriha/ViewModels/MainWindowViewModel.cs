using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiriha.Models;
using Kiriha.Services;

namespace Kiriha.ViewModels;

/// <summary>タブの集合・固定タブの永続化・共有表示オプションを管理するメインウィンドウの ViewModel。</summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly FolderViewSettingsService _folderViewSettings;

    /// <summary>コンパクトビューの全タブ伝播中に PropertyChanged の再入で多重処理しないためのフラグ。</summary>
    private bool _isPropagatingCompactView;

    /// <summary>共有アプリ設定（UpdateService が IgnoreUpdateTag を読み書きする）。</summary>
    public AppSettings Settings => _settings;

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    /// <summary>左ペイン項目（SidebarHeader / SidebarLink の混在リスト）。</summary>
    public ObservableCollection<object> SidebarItems { get; } = new();

    /// <summary>全タブ共通の表示オプション（隠しファイル / 拡張子 / チェックボックス）。</summary>
    public ShellOptions Options { get; }

    /// <summary>お気に入りの内容（settings.json に永続化。左ペインの「お気に入り」表示のツリーが束縛する）。</summary>
    public ObservableCollection<BookmarkNode> BookmarkItems { get; } = new();

    [ObservableProperty]
    private TabViewModel? _selectedTab;

    /// <summary>ウィンドウタイトル（現在のフォルダー名 - Kiriha、タスクバー表示用）。</summary>
    public string WindowTitle
        => SelectedTab is { } tab && tab.Title.Length > 0 ? $"{tab.Title} - Kiriha" : "Kiriha";

    /// <summary>選択タブが変わったときの処理をここへ集約する
    /// （<c>[ObservableProperty]</c> は 1 引数版と 2 引数版の両方を呼ぶため、分けると追いにくい）。
    ///
    /// 離れるタブのギャラリー動画は止め、戻ってきたタブは再開する
    /// （非表示のタブで音が鳴り続けるのを防ぐ）。</summary>
    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        // 並べ替え中は ListBox の選択バインディングが一時的に選択を落とす（→直後に復元する）。
        // その往復に反応してツリー同期や動画の停止・再開を走らせると、同期の折り返しで
        // 選択中タブが別フォルダーへ飛ばされるなどの副作用が出るため、確定するまで何もしない。
        if (_isMovingTab)
        {
            return;
        }

        OnPropertyChanged(nameof(WindowTitle));
        newValue?.EnsureCurrentPathAvailable();
        if (SidebarTreeSyncActive)
        {
            _ = SyncSidebarTreeToCurrentPathAsync();
        }

        oldValue?.SuspendGalleryVideo();
        newValue?.ResumeGalleryVideo();

        // 全画面はギャラリー表示中だけの状態。別のタブへ移ったら通常のウィンドウへ戻す。
        if (newValue is not { IsGalleryView: true })
        {
            IsGalleryFullScreen = false;
        }

        NotifyGalleryEdgeToEdge();
    }

    /// <summary>ステータスバーの表示状態（表示メニューで切替）。</summary>
    [ObservableProperty]
    private bool _showStatusBar = true;

    [RelayCommand]
    private void ToggleStatusBar() => ShowStatusBar = !ShowStatusBar;

    /// <summary>プレビューペインの幅（Thumb ドラッグで変更）。</summary>
    [ObservableProperty]
    private double _previewWidth = 280;

    /// <summary>ギャラリー表示の下部サムネイルストリップの高さ（Thumb ドラッグで変更、全タブ共通で永続化）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GalleryThumbSize))]
    private double _galleryStripHeight = 116;

    /// <summary>ストリップ高さに追従するサムネイル一辺のサイズ（枠・余白・スクロールバー分を差し引く）。</summary>
    public double GalleryThumbSize => Math.Clamp(GalleryStripHeight - 36, 18, 424);

    /// <summary>ギャラリーの全画面表示。画像だけを画面いっぱいに出し、コントロールバーと
    /// 閉じるボタンはマウスを動かしている間だけオーバーレイで見せる（永続化しない一時的な状態）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGalleryChromeVisible))]
    private bool _isGalleryFullScreen;

    /// <summary>全画面表示でないこと。フィルムストリップやステータスバーなど、全画面で畳む
    /// 部品の <c>IsVisible</c> 用（<c>MultiBinding</c> では否定を挟めないため肯定形で持つ）。</summary>
    public bool IsGalleryChromeVisible => !IsGalleryFullScreen;

    /// <summary>ウィンドウが最大化されている。<c>MainWindow</c> が WindowState の変化ごとに書き込む。</summary>
    [ObservableProperty]
    private bool _isWindowMaximized;

    partial void OnIsWindowMaximizedChanged(bool value) => NotifyGalleryEdgeToEdge();

    partial void OnIsGalleryFullScreenChanged(bool value) => NotifyGalleryEdgeToEdge();

    /// <summary>
    /// ギャラリーを画面の端まで使う。
    ///
    /// 通常のコンテンツ島は Chrome ふうに 6px の余白と角丸・1px の輪郭を持つが、
    /// 画面いっぱいに映像を見たいときはそれがディスプレイの端に走る「枠」に見える
    /// （OS のウィンドウ枠ではなくアプリが描いているもの）。全画面表示のあいだと、
    /// 最大化してギャラリーを見ているあいだだけ余白・角丸・輪郭を落とす。
    /// </summary>
    public bool IsGalleryEdgeToEdge
        => IsGalleryFullScreen || (IsWindowMaximized && SelectedTab is { IsGalleryView: true });

    /// <summary>コンテンツ島の外周余白。</summary>
    public Thickness ContentIslandMargin => IsGalleryEdgeToEdge ? default : new Thickness(0, 6, 6, 6);

    /// <summary>コンテンツ島の角丸。</summary>
    public CornerRadius ContentIslandCornerRadius => IsGalleryEdgeToEdge ? default : new CornerRadius(10);

    /// <summary>コンテンツ島の輪郭線を出すか。</summary>
    public bool IsContentIslandOutlineVisible => !IsGalleryEdgeToEdge;

    /// <summary>ギャラリーのメイン画像領域の余白。</summary>
    public Thickness GalleryImageMargin => IsGalleryEdgeToEdge ? default : new Thickness(18, 18, 18, 6);

    private void NotifyGalleryEdgeToEdge()
    {
        OnPropertyChanged(nameof(IsGalleryEdgeToEdge));
        OnPropertyChanged(nameof(ContentIslandMargin));
        OnPropertyChanged(nameof(ContentIslandCornerRadius));
        OnPropertyChanged(nameof(IsContentIslandOutlineVisible));
        OnPropertyChanged(nameof(GalleryImageMargin));
    }

    /// <summary>閉じたタブのパス履歴（Ctrl+Shift+T で開き直す）。</summary>
    private readonly Stack<string> _closedTabPaths = new();

    /// <summary>左ペインの表示状態。</summary>
    [ObservableProperty]
    private bool _showSidebar = true;

    /// <summary>左ペインの幅（Thumb ドラッグで変更、永続化）。</summary>
    [ObservableProperty]
    private double _sidebarWidth = 230;

    /// <summary>垂直タブバーの幅（Thumb ドラッグで変更、永続化）。</summary>
    [ObservableProperty]
    private double _verticalTabWidth = 240;

    /// <summary>検索ボックスの幅（境界の Thumb ドラッグで変更、永続化）。</summary>
    [ObservableProperty]
    private double _searchBoxWidth = 200;

    /// <summary>プレビューペインの表示状態（Alt+P）。</summary>
    [ObservableProperty]
    private bool _showPreviewPane;

    partial void OnShowSidebarChanged(bool value)
    {
        _settings.ShowSidebar = value;
        SettingsService.Save(_settings);
    }

    partial void OnShowStatusBarChanged(bool value)
    {
        _settings.ShowStatusBar = value;
        SettingsService.Save(_settings);
    }

    /// <summary>左ペインの表示内容（クイックアクセス / ツリー / お気に入りの 3 択、永続化）。
    /// bool を並べると「ツリーかつお気に入り」という不正状態を作れてしまうため単一の列挙で持つ。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarQuickAccess))]
    [NotifyPropertyChangedFor(nameof(IsSidebarTree))]
    [NotifyPropertyChangedFor(nameof(IsSidebarBookmarks))]
    private Models.SidebarMode _sidebarMode;

    /// <summary>チップ（ToggleButton）用の 1 モード 1 プロパティ。true を書いたときだけモードを移す
    /// （選択中のチップをもう一度押しても外れない = ラジオボタン相当の振る舞い）。</summary>
    public bool IsSidebarQuickAccess
    {
        get => SidebarMode == Models.SidebarMode.QuickAccess;
        set { if (value) { SidebarMode = Models.SidebarMode.QuickAccess; } else { OnPropertyChanged(); } }
    }

    public bool IsSidebarTree
    {
        get => SidebarMode == Models.SidebarMode.Tree;
        set { if (value) { SidebarMode = Models.SidebarMode.Tree; } else { OnPropertyChanged(); } }
    }

    public bool IsSidebarBookmarks
    {
        get => SidebarMode == Models.SidebarMode.Bookmarks;
        set { if (value) { SidebarMode = Models.SidebarMode.Bookmarks; } else { OnPropertyChanged(); } }
    }

    /// <summary>ツリー表示のルート（デスクトップ 1 ノード。子は展開時に遅延列挙）。</summary>
    public ObservableCollection<Models.FolderTreeNode> SidebarTreeRoots { get; } = [];

    /// <summary>ツリービューの選択ノード（TreeView.SelectedItem と双方向。プログラム側からの現在地同期にも使う）。</summary>
    [ObservableProperty]
    private Models.FolderTreeNode? _sidebarTreeSelectedItem;

    partial void OnSidebarModeChanged(Models.SidebarMode value)
    {
        _settings.SidebarMode = value.ToString();
        SettingsService.Save(_settings);
        if (value == Models.SidebarMode.Tree)
        {
            EnsureSidebarTree();
            if (SidebarTreeSyncActive)
            {
                _ = SyncSidebarTreeToCurrentPathAsync();
            }
        }
    }

    /// <summary>ツリーを現在のフォルダーへ自動追従させる（VS のソリューションエクスプローラーの
    /// 「アクティブ ドキュメントとの同期」と同じトグル）。オンにした瞬間にも 1 回同期する。</summary>
    [ObservableProperty]
    private bool _sidebarTreeSyncActive = true;

    partial void OnSidebarTreeSyncActiveChanged(bool value)
    {
        _settings.SidebarTreeSyncActive = value;
        SettingsService.Save(_settings);
        if (value)
        {
            _ = SyncSidebarTreeToCurrentPathAsync();
        }
    }

    /// <summary>同期処理の世代。ナビゲーション連打時に古い同期結果で選択を上書きしない。</summary>
    private int _treeSyncGeneration;

    /// <summary>「現在地同期」でアプリ側からツリー選択を動かしている間の入れ子数。
    /// TreeView.SelectionChanged はユーザーのクリックだけでなく、この同期やノード展開に伴う
    /// 再通知でも発火する。ユーザー操作と取り違えると選択中タブが別フォルダーへ移動し、
    /// 固定タブでは「移動先を新しいタブで開く」規則が働いてタブが増えてしまう。</summary>
    private int _sidebarTreeSyncDepth;

    /// <summary>同期中（アプリ側がツリー選択を動かしている最中）かどうか。</summary>
    internal bool IsSyncingSidebarTree => _sidebarTreeSyncDepth > 0;

    /// <summary>同期でアプリ側が選んだノード。同じノードの SelectionChanged が折り返し通知された
    /// ときに 1 回だけ読み捨てるための引換券（同期完了後のレイアウトで届く分を拾う）。</summary>
    private Models.FolderTreeNode? _syncedTreeNodeEcho;

    /// <summary>アプリ側の選択に対する折り返し通知なら true を返して引換券を消費する。</summary>
    internal bool TryConsumeSyncedTreeNodeEcho(Models.FolderTreeNode node)
    {
        if (!ReferenceEquals(node, _syncedTreeNodeEcho))
        {
            return false;
        }

        _syncedTreeNodeEcho = null;
        return true;
    }

    /// <summary>選択中タブの現在フォルダーまでツリーを展開して選択状態にする。
    /// 呼び出し元は「アクティブ ドキュメントとの同期」（<see cref="SidebarTreeSyncActive"/>）が
    /// オンのときのナビゲーション・タブ切り替え、トグルをオンへ切り替えた瞬間、
    /// ツリー表示をオンにした瞬間の初期位置決め。</summary>
    public async Task SyncSidebarTreeToCurrentPathAsync()
    {
        _sidebarTreeSyncDepth++;
        try
        {
            await SyncSidebarTreeToCurrentPathCoreAsync();
        }
        catch (Exception ex)
        {
            // 呼び出し元は fire-and-forget のため、ここで握りつぶさず必ずログへ残す
            Logger.LogException("サイドバーツリーの現在地同期に失敗しました", ex);
        }
        finally
        {
            _sidebarTreeSyncDepth--;
        }
    }

    private async Task SyncSidebarTreeToCurrentPathCoreAsync()
    {
        if (!IsSidebarTree || SidebarTreeRoots.Count == 0
            || SelectedTab is not { IsSettingsTab: false } tab)
        {
            return;
        }

        var path = tab.CurrentPath;
        var generation = Interlocked.Increment(ref _treeSyncGeneration);
        var node = SidebarTreeRoots[0];

        // PC（ドライブ一覧）はルートそのもの
        if (path == FileSystemService.ComputerPath)
        {
            SelectTreeNode(node);
            return;
        }

        node.IsExpanded = true;
        await node.EnsureChildrenAsync();
        if (generation != _treeSyncGeneration)
        {
            return;
        }

        // ルートの PC からドライブ → 実フォルダーへと、物理パスをそのまま 1 段ずつ降りる
        while (!WindowsPathIdentity.Instance.Equals(node.Path, path))
        {
            var next = node.Children.FirstOrDefault(c => IsSelfOrAncestorOf(c.Path, path));
            if (next is null)
            {
                Logger.Log(
                    $"ツリー同期: {node.Name} (子 {node.Children.Count} 件) から {path} へ降下できませんでした",
                    LogLevel.Warning);
                return;
            }

            node = next;
            node.IsExpanded = true;
            await node.EnsureChildrenAsync();
            if (generation != _treeSyncGeneration)
            {
                return;
            }
        }

        SelectTreeNode(node);
    }

    private void SelectTreeNode(Models.FolderTreeNode? node)
    {
        if (node is null)
        {
            return;
        }

        node.IsExpanded = true;
        if (ReferenceEquals(SidebarTreeSelectedItem, node))
        {
            return;
        }

        _syncedTreeNodeEcho = node;
        SidebarTreeSelectedItem = node;
    }

    /// <summary>candidate が target 自身またはその祖先ディレクトリかどうか。</summary>
    private static bool IsSelfOrAncestorOf(string candidate, string target)
    {
        if (candidate.Length == 0)
        {
            return false;
        }

        // ルート ("C:\") は TrimEndingDirectorySeparator で区切りが残るため、二重付与しないように整える
        var prefix = Path.TrimEndingDirectorySeparator(candidate);
        if (!prefix.EndsWith(Path.DirectorySeparatorChar))
        {
            prefix += Path.DirectorySeparatorChar;
        }

        return WindowsPathIdentity.Instance.Equals(candidate, target)
               || target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureSidebarTree()
    {
        if (SidebarTreeRoots.Count > 0)
        {
            return;
        }

        // ツリーの階層は物理パスと一致させる（PC > C: > Users > … ）。仮想フォルダーは挟まない。
        var root = new Models.FolderTreeNode
        {
            Name = LocalizationService.Text("Text.Tree.Computer"),
            Path = FileSystemService.ComputerPath,
            Icon = "💻",
            Kind = Models.FolderTreeNode.NodeKind.Computer,
        };
        SidebarTreeRoots.Add(root);
        // 既定でドライブ一覧まで開く（子はここで遅延ロードされる）
        root.IsExpanded = true;
    }

    // ===== ツリーのヘッダー操作（VSCode のエクスプローラーと同じ 4 ボタン） =====

    /// <summary>表示中のインライン新規作成入力行（無ければ null）。</summary>
    private Models.NewTreeItemNode? _treeInputNode;

    /// <summary>入力行を挿入した親ノード（作成先フォルダー）。</summary>
    private Models.FolderTreeNode? _treeInputParent;

    /// <summary>確定処理の再入ガード（Enter の確定中に LostFocus の確定が重ならないように）。</summary>
    private bool _isCommittingNewTreeItem;

    /// <summary>
    /// 「新しいファイル / 新しいフォルダー」の入力行をツリーへ挿入する。
    /// VSCode と同じく、選択中のフォルダーの直下へ作成する。ルートの PC はドライブ一覧であって
    /// 実在するフォルダーではないため、そこが選択されているときは何もしない。
    /// </summary>
    /// <param name="node">
    /// 作成先フォルダー。右クリックメニューから呼ぶときは、選択とは別の対象になり得るため明示する。
    /// null なら従来どおり選択中のノードへ作成する。
    /// </param>
    public async Task BeginNewTreeItemAsync(bool isFile, Models.FolderTreeNode? node = null)
    {
        if (!IsSidebarTree || SidebarTreeRoots.Count == 0)
        {
            return;
        }

        CancelNewTreeItem();
        if ((node ?? SidebarTreeSelectedItem) is not Models.FolderTreeNode { Path.Length: > 0 } target
            || target is Models.NewTreeItemNode)
        {
            return;
        }

        // 展開・子の列挙で発火する SelectionChanged をユーザー操作と取り違えないようにする
        _sidebarTreeSyncDepth++;
        try
        {
            target.IsExpanded = true;
            await target.EnsureChildrenAsync();
        }
        catch (Exception ex)
        {
            Logger.LogException($"新規作成先の子フォルダーを列挙できませんでした: {target.Path}", ex);
        }
        finally
        {
            _sidebarTreeSyncDepth--;
        }

        var input = new Models.NewTreeItemNode
        {
            Name = "",
            Path = "",
            Icon = isFile ? "📄" : "📁",
            IsFile = isFile,
        };
        input.PropertyChanged += TreeInputNode_PropertyChanged;
        _treeInputNode = input;
        _treeInputParent = target;
        target.Children.Insert(0, input);
    }

    /// <summary>入力中の逐次検証（VSCode と同じく、無効な名前や重複はその場でエラー表示する）。</summary>
    private void TreeInputNode_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.NewTreeItemNode.EditText)
            && sender is Models.NewTreeItemNode node
            && ReferenceEquals(node, _treeInputNode)
            && _treeInputParent is { } parent)
        {
            node.ValidationError = ValidateNewTreeItemName(parent.Path, node.EditText);
        }
    }

    /// <summary>入力行を確定せずに取り除く（Esc・別操作の開始時）。</summary>
    public void CancelNewTreeItem()
    {
        if (_treeInputNode is not { } node)
        {
            return;
        }

        node.PropertyChanged -= TreeInputNode_PropertyChanged;
        _treeInputParent?.Children.Remove(node);
        _treeInputNode = null;
        _treeInputParent = null;
    }

    /// <summary>
    /// 入力行を確定して実際に作成する。VSCode と同じく「a/b/c」のような入力は中間フォルダーごと作る。
    /// 戻り値 false は入力継続（検証エラーや作成失敗をその場に表示したまま）。
    /// </summary>
    public async Task<bool> CommitNewTreeItemAsync()
    {
        if (_isCommittingNewTreeItem)
        {
            return true;
        }

        if (_treeInputNode is not { } node || _treeInputParent is not { } parent)
        {
            return true;
        }

        var name = node.EditText.Trim();
        if (name.Length == 0)
        {
            node.ValidationError = LocalizationService.Text("Text.Tree.NewItemEmpty");
            return false;
        }

        if (ValidateNewTreeItemName(parent.Path, name) is { } error)
        {
            node.ValidationError = error;
            return false;
        }

        _isCommittingNewTreeItem = true;
        try
        {
            var segments = name.Split(TreeInputSeparators, StringSplitOptions.RemoveEmptyEntries);
            var isFile = node.IsFile;
            try
            {
                await Task.Run(() =>
                {
                    var dirPath = isFile
                        ? Path.Combine([parent.Path, .. segments[..^1]])
                        : Path.Combine([parent.Path, .. segments]);
                    Directory.CreateDirectory(dirPath);
                    if (isFile)
                    {
                        // CreateNew: 検証後に外から同名ファイルが作られていても上書きしない
                        using var stream = new FileStream(
                            Path.Combine(dirPath, segments[^1]), FileMode.CreateNew);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogException($"ツリーからの新規作成に失敗しました: {parent.Path} / {name}", ex);
                node.ValidationError = LocalizationService.Text("Text.Tree.CreateFailed", ex.Message);
                return false;
            }

            // 成功: 入力行を外し、親を再列挙して作成したフォルダーを選択状態で見せる
            // （ファイル自体はツリーに出ないため、フォルダー階層ぶんだけ辿る）
            CancelNewTreeItem();
            _sidebarTreeSyncDepth++;
            try
            {
                await parent.ReloadChildrenAsync();
                var current = parent;
                var walked = parent.Path;
                Models.FolderTreeNode? reveal = null;
                var folderDepth = isFile ? segments.Length - 1 : segments.Length;
                for (var i = 0; i < folderDepth; i++)
                {
                    walked = Path.Combine(walked, segments[i]);
                    await current.EnsureChildrenAsync();
                    var child = current.Children.FirstOrDefault(
                        c => WindowsPathIdentity.Instance.Equals(c.Path, walked));
                    if (child is null)
                    {
                        break;
                    }

                    reveal = child;
                    current = child;
                }

                SelectTreeNode(reveal);
            }
            finally
            {
                _sidebarTreeSyncDepth--;
            }

            return true;
        }
        finally
        {
            _isCommittingNewTreeItem = false;
        }
    }

    private static readonly char[] TreeInputSeparators = ['/', '\\'];

    /// <summary>Windows で予約されているデバイス名（拡張子を付けても不可）。</summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>VSCode と同じ観点の名前検証（空 / 無効文字 / 予約名 / 既存）。問題なければ null。</summary>
    private static string? ValidateNewTreeItemName(string parentPath, string input)
    {
        var name = input.Trim();
        if (name.Length == 0)
        {
            return LocalizationService.Text("Text.Tree.NewItemEmpty");
        }

        var segments = name.Split(TreeInputSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return LocalizationService.Text("Text.Tree.NewItemInvalid", name);
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || ReservedDeviceNames.Contains(
                    segment.Split('.')[0].TrimEnd(), StringComparer.OrdinalIgnoreCase))
            {
                return LocalizationService.Text("Text.Tree.NewItemInvalid", segment);
            }
        }

        var fullPath = Path.Combine([parentPath, .. segments]);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return LocalizationService.Text("Text.Tree.NewItemExists", name);
        }

        return null;
    }

    /// <summary>
    /// ツリーを再列挙する（「最新の情報に更新」）。展開状態と選択パスは可能な範囲で引き継ぐ。
    /// ツリーはファイルシステムの監視を持たないため、これが唯一の内容更新経路。
    /// </summary>
    public async Task RefreshSidebarTreeAsync()
    {
        if (SidebarTreeRoots.Count == 0)
        {
            return;
        }

        CancelNewTreeItem();
        var selectedPath = SidebarTreeSelectedItem?.Path;
        _sidebarTreeSyncDepth++;
        try
        {
            var root = SidebarTreeRoots[0];
            await ReloadNodeRecursivelyAsync(root, root);
            if (selectedPath is { Length: > 0 }
                && FindLoadedNodeByPath(root, selectedPath) is { } node)
            {
                _syncedTreeNodeEcho = node;
                SidebarTreeSelectedItem = node;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("ツリーの再列挙に失敗しました", ex);
        }
        finally
        {
            _sidebarTreeSyncDepth--;
        }
    }

    /// <summary>fresh の子を列挙し直し、previous（再列挙前の同じパスのノード）の展開状態を
    /// 同じパスの新しい子へ引き継ぐ。列挙済みだった子は同様に再帰で列挙し直す。</summary>
    private static async Task ReloadNodeRecursivelyAsync(
        Models.FolderTreeNode fresh, Models.FolderTreeNode previous)
    {
        if (!previous.HasLoadedChildren)
        {
            return;
        }

        var old = new Dictionary<string, Models.FolderTreeNode>(WindowsPathIdentity.Instance);
        foreach (var child in previous.Children)
        {
            if (ReloadKeyOf(child) is { } key)
            {
                old[key] = child;
            }
        }

        await fresh.ReloadChildrenAsync();
        foreach (var child in fresh.Children)
        {
            if (ReloadKeyOf(child) is not { } key || !old.TryGetValue(key, out var prev))
            {
                continue;
            }

            if (prev.HasLoadedChildren)
            {
                await ReloadNodeRecursivelyAsync(child, prev);
            }

            child.IsExpanded = prev.IsExpanded;
        }
    }

    /// <summary>再列挙の前後でノードを対応付けるキー。マイ コンピュータは Path が空
    /// （<see cref="FileSystemService.ComputerPath"/>）なので Kind で照合する。
    /// プレースホルダーや入力行（どちらも Path が空の Folder）は対応付けない。</summary>
    private static string? ReloadKeyOf(Models.FolderTreeNode node)
        => node.Path.Length > 0
            ? node.Path
            : node.Kind == Models.FolderTreeNode.NodeKind.Computer ? "\0computer" : null;

    /// <summary>読み込み済みのノードから path に一致するものを探す（見つからなければ null）。</summary>
    private static Models.FolderTreeNode? FindLoadedNodeByPath(Models.FolderTreeNode node, string path)
    {
        if (node.Path.Length > 0 && WindowsPathIdentity.Instance.Equals(node.Path, path))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (FindLoadedNodeByPath(child, path) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>ツリーの全ノードを畳む。ルートのデスクトップは VSCode のワークスペースルートに
    /// 相当するため展開したまま残す（VSCode の「フォルダーを折りたたむ」と同じ見え方）。</summary>
    public void CollapseSidebarTree()
    {
        CancelNewTreeItem();
        _sidebarTreeSyncDepth++;
        try
        {
            foreach (var root in SidebarTreeRoots)
            {
                foreach (var child in root.Children)
                {
                    CollapseRecursively(child);
                }
            }
        }
        finally
        {
            _sidebarTreeSyncDepth--;
        }
    }

    private static void CollapseRecursively(Models.FolderTreeNode node)
    {
        foreach (var child in node.Children)
        {
            CollapseRecursively(child);
        }

        node.IsExpanded = false;
    }

    partial void OnPreviewWidthChanged(double value)
    {
        _settings.PreviewWidth = value; // 保存自体は終了時の SaveWindowBounds でまとめて行う
    }

    partial void OnGalleryStripHeightChanged(double value)
    {
        _settings.GalleryStripHeight = value; // 保存自体は終了時の SaveWindowBounds でまとめて行う
    }

    partial void OnSidebarWidthChanged(double value)
    {
        _settings.SidebarWidth = value; // 保存自体は終了時の SaveWindowBounds でまとめて行う
    }

    /// <summary>垂直タブバーの幅の下限。ファイル一覧を広く使いたいときのために、
    /// タブ名がほとんど読めなくなるところまで縮められるようにしてある
    /// （アイコンと閉じるボタンが並ぶ幅が実質の下限）。</summary>
    public const double MinVerticalTabWidth = 96;

    public const double MaxVerticalTabWidth = 420;

    partial void OnVerticalTabWidthChanged(double value)
    {
        _settings.VerticalTabWidth = value; // 保存自体は終了時の SaveWindowBounds でまとめて行う
    }

    partial void OnSearchBoxWidthChanged(double value)
    {
        _settings.SearchBoxWidth = value; // 保存自体は終了時の SaveWindowBounds でまとめて行う
    }

    partial void OnShowPreviewPaneChanged(bool value)
    {
        _settings.ShowPreviewPane = value;
        SettingsService.Save(_settings);
        foreach (var tab in Tabs)
        {
            tab.SetPreviewEnabled(value);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => ShowSidebar = !ShowSidebar;

    [RelayCommand]
    private void TogglePreviewPane() => ShowPreviewPane = !ShowPreviewPane;

    /// <summary>テーマ設定（System / Light / Dark）。設定タブの ComboBox から変更。</summary>
    public string OptTheme
    {
        get => _settings.ThemePreference;
        set
        {
            _settings.ThemePreference = value;
            SettingsService.Save(_settings);
            ApplyTheme(value);
            OnPropertyChanged();
        }
    }

    private void ApplyTheme(string preference)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = preference switch
            {
                "Light" => Avalonia.Styling.ThemeVariant.Light,
                "Dark" => Avalonia.Styling.ThemeVariant.Dark,
                "OneDark" => Services.ThemeService.OneDark,
                "Dim" => Services.ThemeService.Dim,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };

            // テーマ（明暗）が変わると背景の基準色も変わるため、アクリル半透明色を現在の設定で再計算する
            Services.ThemeService.SetAcrylicEnabled(app, _settings.UseAcrylicBackground);
        }
    }

    /// <summary>設定タブ: ウィンドウのアクリル（半透明ぼかし）効果（Lhamiel / RealTimeTranslator 同等）。</summary>
    public bool OptUseAcrylicBackground
    {
        get => _settings.UseAcrylicBackground;
        set
        {
            _settings.UseAcrylicBackground = value;
            SettingsService.Save(_settings);
            if (Avalonia.Application.Current is { } app)
            {
                Services.ThemeService.SetAcrylicEnabled(app, value);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: ギャラリー表示の鮮鋭化（RCAS）。切り替えたら、見ている画像にも即座に反映する。</summary>
    public bool OptSharpenGallery
    {
        get => _settings.SharpenGallery;
        set
        {
            _settings.SharpenGallery = value;
            SettingsService.Save(_settings);
            Services.ContrastAdaptiveSharpenService.Enabled = value;
            // 動画は次のフレームから変わるが、静止画は表示済みのビットマップが残るので読み直す。
            foreach (var tab in Tabs)
            {
                tab.ReloadGalleryPreview();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: 鮮鋭化の強さ（Low / Normal / High）。切り替えたら今の画像へも即座に反映する。</summary>
    public string OptSharpenStrength
    {
        get => _settings.SharpenStrength;
        set
        {
            _settings.SharpenStrength = value;
            SettingsService.Save(_settings);
            Services.ContrastAdaptiveSharpenService.Strength = ParseSharpenStrength(value);
            foreach (var tab in Tabs)
            {
                tab.ReloadGalleryPreview();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: 動画の早送り・巻き戻し 1 回あたりの秒数（コントロールバーのボタンと ← / →）。</summary>
    public double OptVideoSeekSeconds
    {
        get => _settings.VideoSeekSeconds;
        set
        {
            var clamped = Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.1, 60.0);
            _settings.VideoSeekSeconds = clamped;
            SettingsService.Save(_settings);
            TabViewModel.SeekStepSeconds = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: Kiriha 内で画像をダブルクリックしたらギャラリーの全画面表示で開く。</summary>
    public bool OptOpenImagesInGallery
    {
        get => _settings.OpenImagesInGallery;
        set
        {
            _settings.OpenImagesInGallery = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: Kiriha 内で動画をダブルクリックしたらギャラリーの全画面表示で開く。</summary>
    public bool OptOpenVideosInGallery
    {
        get => _settings.OpenVideosInGallery;
        set
        {
            _settings.OpenVideosInGallery = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定文字列から強さへ。未知の値（手書きされた settings.json）は標準に倒す。</summary>
    private static Services.SharpenStrength ParseSharpenStrength(string value)
        => Enum.TryParse<Services.SharpenStrength>(value, ignoreCase: true, out var parsed)
            ? parsed
            : Services.SharpenStrength.Normal;

    /// <summary>設定タブ: タブのダブルクリック動作（None / Pin / Close）。</summary>
    public string OptTabDoubleClickAction
    {
        get => _settings.TabDoubleClickAction;
        set
        {
            _settings.TabDoubleClickAction = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: タブのホイールクリック動作（None / Pin / Close）。</summary>
    public string OptTabMiddleClickAction
    {
        get => _settings.TabMiddleClickAction;
        set
        {
            _settings.TabMiddleClickAction = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: フォルダー背景のダブルクリック動作（None / Up / Refresh）。</summary>
    public string OptBackgroundDoubleClickAction
    {
        get => _settings.BackgroundDoubleClickAction;
        set
        {
            _settings.BackgroundDoubleClickAction = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: フォルダー背景のホイールクリック動作（None / Up / Refresh）。</summary>
    public string OptBackgroundMiddleClickAction
    {
        get => _settings.BackgroundMiddleClickAction;
        set
        {
            _settings.BackgroundMiddleClickAction = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: フォルダーツリーからのドラッグを禁止。</summary>
    public bool OptSidebarTreeDragDisabled
    {
        get => _settings.SidebarTreeDragDisabled;
        set
        {
            _settings.SidebarTreeDragDisabled = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: フォルダーツリーへのドロップを禁止。</summary>
    public bool OptSidebarTreeDropDisabled
    {
        get => _settings.SidebarTreeDropDisabled;
        set
        {
            _settings.SidebarTreeDropDisabled = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: 起動フォルダー。</summary>
    public string OptStartupPath
    {
        get => _settings.StartupPath;
        set
        {
            _settings.StartupPath = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: 前回のタブを復元。</summary>
    public bool OptRestoreAllTabs
    {
        get => _settings.RestoreAllTabs;
        set
        {
            _settings.RestoreAllTabs = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: ウィンドウのサイズと位置を保存して次回復元する。</summary>
    public bool OptRememberWindowBounds
    {
        get => _settings.RememberWindowBounds;
        set
        {
            _settings.RememberWindowBounds = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: 最小化時にタスクバーではなくタスクトレイに格納する（Discord 相当）。</summary>
    public bool OptMinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            _settings.MinimizeToTray = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: 起動時にウィンドウを表示せずタスクトレイに格納した状態で開始する（Discord 相当）。</summary>
    public bool OptStartMinimizedToTray
    {
        get => _settings.StartMinimizedToTray;
        set
        {
            _settings.StartMinimizedToTray = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: Windows のスタートアップに登録する（真実の源は HKCU Run キー）。</summary>
    public bool OptRunAtStartup
    {
        get => WindowsIntegrationService.IsStartupEnabled();
        set
        {
            WindowsIntegrationService.SetStartupEnabled(value);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: エクスプローラーの右クリックメニューに「Kiriha で開く」を追加する（真実の源は HKCU レジストリ）。</summary>
    public bool OptExplorerContextMenu
    {
        get => WindowsIntegrationService.IsExplorerMenuEnabled();
        set
        {
            WindowsIntegrationService.SetExplorerMenuEnabled(value);
            OnPropertyChanged();
        }
    }

    /// <summary>設定タブ: フォルダーとドライブを開く既定アプリを Kiriha にする。</summary>
    public bool OptDefaultFolderApp
    {
        get => WindowsIntegrationService.IsDefaultFolderAppEnabled();
        set
        {
            _ = WindowsIntegrationService.SetDefaultFolderAppEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(OptDefaultFolderAppStatus));
        }
    }

    public string OptDefaultFolderAppStatus => OptDefaultFolderApp
        ? LocalizationService.Text("Text.Settings.DefaultFolderApp.On")
        : LocalizationService.Text("Text.Settings.DefaultFolderApp.Off");

    /// <summary>設定タブ: 設定を既定値に戻す（固定タブとお気に入りは保持）。</summary>
    [RelayCommand]
    private void ResetSettings()
    {
        OptShowHidden = false;
        OptShowExtensions = false;
        OptShowCheckBoxes = false;
        OptIconSet = nameof(FileIconSet.Original);
        OptCheckUpdatesOnStartup = true;
        OptRestoreAllTabs = false;
        OptStartupPath = "";
        OptTheme = "System";
        OptUseAcrylicBackground = true;
        OptRememberWindowBounds = true;
        OptRunAtStartup = false;
        OptExplorerContextMenu = false;
        OptDefaultFolderApp = false;
        OptMinimizeToTray = false;
        OptStartMinimizedToTray = false;
        ShowSidebar = true;
        SidebarMode = Models.SidebarMode.QuickAccess;
        OptSidebarTreeDragDisabled = false;
        OptSidebarTreeDropDisabled = false;
        OptTabDoubleClickAction = "None";
        OptTabMiddleClickAction = "Close";
        OptBackgroundDoubleClickAction = "None";
        OptBackgroundMiddleClickAction = "None";
        SidebarWidth = 230;
        VerticalTabWidth = 240;
        SearchBoxWidth = 200;
        ShowPreviewPane = false;
        PreviewWidth = 280;
        GalleryStripHeight = 116;
        ShowStatusBar = true;

        // コンパクトビューは開いているタブへも反映する（タブ側の変更通知が _settings.CompactView も既定へ戻す）
        foreach (var tab in Tabs.Where(t => !t.IsSettingsTab))
        {
            tab.IsCompactView = false;
        }

        _settings.CompactView = false;

        // 詳細表示の列幅・列の表示/非表示・既定の表示モード/アイコンサイズ/並べ替えも既定へ戻す
        // （AppSettings の初期値と一致させる。新規タブに反映され、Save は末尾でまとめて行う）。
        _settings.ColNameWidth = 300;
        _settings.ColModifiedWidth = 160;
        _settings.ColCreatedWidth = 170;
        _settings.ColTypeWidth = 140;
        _settings.ColSizeWidth = 100;
        _settings.ShowColModified = true;
        _settings.ShowColCreated = false;
        _settings.ShowColType = true;
        _settings.ShowColSize = true;
        _settings.DefaultViewMode = "Details";
        _settings.DefaultIconSize = 28;
        _settings.DefaultSortKey = "Name";
        _settings.DefaultSortAscending = true;
        _folderViewSettings.Clear();
        _folderViewSettings.Flush();
        SettingsService.Save(_settings);
    }

    /// <summary>設定タブ: ログフォルダーを開く。</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kiriha", "logs");
        try
        {
            Directory.CreateDirectory(dir);
            TrustedProcessLauncher.Start("explorer.exe", [dir], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch
        {
            // 開けなくても致命的ではない
        }
    }

    /// <summary>設定タブ: クイックアクセスを再読み込み。</summary>
    [RelayCommand]
    private void ReloadSidebar() => RefreshSidebar();

    // ===== オプションパネル用のバインディングプロパティ =====

    public bool OptShowHidden
    {
        get => Options.ShowHidden;
        set { Options.ShowHidden = value; OnPropertyChanged(); }
    }

    public bool OptShowExtensions
    {
        get => Options.ShowExtensions;
        set { Options.ShowExtensions = value; OnPropertyChanged(); }
    }

    public bool OptShowCheckBoxes
    {
        get => Options.ShowCheckBoxes;
        set { Options.ShowCheckBoxes = value; OnPropertyChanged(); }
    }

    /// <summary>設定タブのアイコンセット選択（表示ラベルは XAML 側の ComboBoxItem、
    /// ここで扱うのは enum 名の文字列。テーマ設定と同じ Tag 方式）。</summary>
    public string? OptIconSet
    {
        get => Options.IconSet.ToString();
        set
        {
            if (!Enum.TryParse<FileIconSet>(value, out var selected) || !Enum.IsDefined(selected)) return;
            if (Options.IconSet == selected) return;
            Options.IconSet = selected;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 右クリックメニューの実装方式。表示ラベルは XAML 側の ComboBoxItem が持ち、
    /// ここで扱うのは enum 名の文字列（アイコンセット・テーマ設定と同じ Tag 方式）。
    /// </summary>
    public string? OptContextMenuStyle
    {
        get => ContextMenuStyle.ToString();
        set
        {
            if (value is null || !Enum.TryParse<ContextMenuStyle>(value, out var style) || !Enum.IsDefined(style)) return;
            if (ContextMenuStyle == style) return;
            ContextMenuStyle = style;
            _settings.ContextMenuStyle = style.ToString();
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>右クリックメニューの実装方式（View が右クリック時に読む）。</summary>
    public ContextMenuStyle ContextMenuStyle { get; private set; } = Models.ContextMenuStyle.Modern;

    // ===== フォルダーの既定表示（表示設定を保存していないフォルダーで使う） =====
    //
    // フォルダーごとの記憶（folder-views.json）が無いフォルダーは、ここで決めた表示方法と
    // 並べ替えで開く。以前は「最後に使った表示方法」を自動で既定にしていたが、設定画面から
    // 明示的に決められるようにしたので、操作に追従して勝手に書き換わらないようにしてある。
    // 値は ViewMode / SortKeys の名前そのままで、表示ラベルは XAML 側の ComboBoxItem が持つ。

    public string? OptDefaultViewMode
    {
        get => _settings.DefaultViewMode;
        set
        {
            if (value is null || !Enum.TryParse<ViewMode>(value, out var mode) || !Enum.IsDefined(mode)) return;
            if (_settings.DefaultViewMode == value) return;
            _settings.DefaultViewMode = value;
            // アイコン系の既定は「最後に使った大きさ」を引き継ぐと意図と食い違うので、
            // 表示方法を選び直したらプリセットの大きさへ揃える（表示メニューと同じ値）。
            _settings.DefaultIconSize = mode switch
            {
                ViewMode.ExtraLargeIcons => 96,
                ViewMode.LargeIcons => 56,
                ViewMode.MediumIcons => 32,
                _ => _settings.DefaultIconSize,
            };
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    public string? OptDefaultSortKey
    {
        get => _settings.DefaultSortKey;
        set
        {
            if (value is not (SortKeys.Name or SortKeys.Modified or SortKeys.Created or SortKeys.Type or SortKeys.Size)) return;
            if (_settings.DefaultSortKey == value) return;
            _settings.DefaultSortKey = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>昇順 / 降順。ドロップダウンで扱うため bool ではなく文字列で持つ。</summary>
    public string? OptDefaultSortOrder
    {
        get => _settings.DefaultSortAscending ? "Ascending" : "Descending";
        set
        {
            if (value is not ("Ascending" or "Descending")) return;
            var ascending = value == "Ascending";
            if (_settings.DefaultSortAscending == ascending) return;
            _settings.DefaultSortAscending = ascending;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>フォルダー別表示設定のリセット結果（設定画面に出す一言）。</summary>
    [ObservableProperty]
    private string _folderViewResetMessage = "";

    /// <summary>フォルダーごとに覚えた表示方法・並べ替え・列幅をすべて捨てる。
    /// 開いているタブにも既定を当て直して、その場で結果が見えるようにする。</summary>
    [RelayCommand]
    private void ResetFolderViewSettings()
    {
        _folderViewSettings.Clear();

        // 既定を当て直す。ApplyFolderViewSettings は適用中フラグで保存を止めるので、
        // ここで消したばかりの記憶が書き戻されることはない。
        foreach (var tab in Tabs.Where(tab => !tab.IsSettingsTab))
        {
            tab.ApplyFolderViewSettings(CreateDefaultFolderViewSettings(tab.CurrentPath));
        }

        FolderViewResetMessage = LocalizationService.Text("Text.Settings.DefaultView.ResetDone");
    }

    /// <summary>設定タブ: 表示言語のドロップダウン項目（対応言語の一覧そのもの）。</summary>
    public IReadOnlyList<Models.Locale> LocaleOptions { get; } = Models.Locale.Supported;

    /// <summary>設定タブ: UI 表示言語。settings.json が空文字（初回インストール直後）のときは
    /// OS の UI 言語から自動判定した言語が選択済みとして見える。</summary>
    public Models.Locale? OptLocale
    {
        get => Models.Locale.Supported.FirstOrDefault(l => l.Key == LocalizationService.CurrentLocale);
        set
        {
            if (value is null || value.Key == LocalizationService.CurrentLocale) return;
            _settings.Locale = value.Key;
            SettingsService.Save(_settings);
            LocalizationService.SetLocale(value.Key);
            OnPropertyChanged();
        }
    }

    public bool OptCheckUpdatesOnStartup
    {
        get => _settings.CheckUpdatesOnStartup;
        set
        {
            _settings.CheckUpdatesOnStartup = value;
            SettingsService.Save(_settings);
            OnPropertyChanged();
        }
    }

    public string VersionText => $"Kiriha {typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";

    /// <summary>Chrome の chrome://settings と同じく、設定を専用タブとして開く（既存があれば選択）。</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var existing = Tabs.FirstOrDefault(t => t.IsSettingsTab);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = AddSettingsTab(pinned: false);
        SelectedTab = tab;
    }

    /// <summary>Ctrl+Shift+T: 最後に閉じたタブを開き直す（Chrome 互換）。</summary>
    [RelayCommand]
    private void ReopenClosedTab()
    {
        while (_closedTabPaths.Count > 0)
        {
            var path = _closedTabPaths.Pop();
            if (path == FileSystemService.ComputerPath || Directory.Exists(path))
            {
                OpenInNewTab(path);
                return;
            }
        }
    }

    /// <summary>Ctrl+Shift+B。左ペインの「お気に入り」表示と、その前に見ていた表示を行き来する
    /// （旧「お気に入りバーの表示切替」の置き換え。ペイン自体が隠れているときは開く）。</summary>
    [RelayCommand]
    private void ToggleBookmarksPane()
    {
        ShowSidebar = true;
        SidebarMode = IsSidebarBookmarks ? Models.SidebarMode.QuickAccess : Models.SidebarMode.Bookmarks;
    }

    /// <summary>お気に入りへ追加したことが分かるように、左ペインをお気に入り表示にして見せる。</summary>
    public void ShowBookmarksPane()
    {
        ShowSidebar = true;
        SidebarMode = Models.SidebarMode.Bookmarks;
    }

    public MainWindowViewModel()
    {
        _settings = SettingsService.Load();
        // 表示言語はウィンドウ構築前に確定させる（MainWindow の DynamicResource が初回解決される前）。
        LocalizationService.SetLocale(_settings.Locale);
        _folderViewSettings = new FolderViewSettingsService();
        Options = new ShellOptions
        {
            ShowHidden = _settings.ShowHidden,
            ShowExtensions = _settings.ShowExtensions,
        };
        Options.ShowCheckBoxes = _settings.ShowCheckBoxes;
        // Enum.TryParse は "5" のような数値文字列も成功扱いにするため IsDefined で未定義値を弾く
        Options.IconSet = Enum.TryParse<FileIconSet>(_settings.IconSet, out var iconSet) && Enum.IsDefined(iconSet)
            ? iconSet
            : _settings.UseMaterialIcons ? FileIconSet.Material : FileIconSet.Original;
        _settings.IconSet = Options.IconSet.ToString();
        _settings.UseMaterialIcons = false;
        ContextMenuStyle = Enum.TryParse<ContextMenuStyle>(_settings.ContextMenuStyle, out var menuStyle)
                           && Enum.IsDefined(menuStyle)
            ? menuStyle
            : Models.ContextMenuStyle.Modern;
        Options.Changed += (_, e) =>
        {
            _settings.ShowHidden = Options.ShowHidden;
            _settings.ShowExtensions = Options.ShowExtensions;
            _settings.ShowCheckBoxes = Options.ShowCheckBoxes;
            _settings.IconSet = Options.IconSet.ToString();
            _settings.UseMaterialIcons = false;
            SettingsService.Save(_settings);
            // サイドバー（クイックアクセス等）とお気に入りのアイコンもセット設定に追従させる
            if (e.Kind == ShellOptionKind.IconSet)
            {
                RefreshSidebar();
                _ = RefreshBookmarkIconsAsync();
            }
        };

        _showSidebar = _settings.ShowSidebar;
        _sidebarWidth = _settings.SidebarWidth is > 120 and < 600 ? Math.Round(_settings.SidebarWidth) : 230;
        _verticalTabWidth = _settings.VerticalTabWidth is >= MinVerticalTabWidth and <= MaxVerticalTabWidth
            ? Math.Round(_settings.VerticalTabWidth)
            : 240;
        // 旧バージョンが保存した小数幅はレイアウト丸めで線がぼやけない整数値へ移行する。
        _settings.SidebarWidth = _sidebarWidth;
        _settings.VerticalTabWidth = _verticalTabWidth;
        _searchBoxWidth = _settings.SearchBoxWidth is > 120 and < 500 ? _settings.SearchBoxWidth : 200;
        _showPreviewPane = _settings.ShowPreviewPane;
        _previewWidth = _settings.PreviewWidth is >= 180 and <= 600 ? _settings.PreviewWidth : 280;
        _galleryStripHeight = _settings.GalleryStripHeight is >= 54 and <= 460 ? _settings.GalleryStripHeight : 116;

        // 鮮鋭化も全タブ・動画で共通の状態なので、設定の値をサービス側へ反映しておく。
        Services.ContrastAdaptiveSharpenService.Enabled = _settings.SharpenGallery;
        Services.ContrastAdaptiveSharpenService.Strength = ParseSharpenStrength(_settings.SharpenStrength);
        TabViewModel.SeekStepSeconds = _settings.VideoSeekSeconds is >= 0.1 and <= 60 ? _settings.VideoSeekSeconds : 1.0;

        // ギャラリー動画の音量・ミュート・速度は全タブ共通かつ次回起動へ持ち越す。
        // タブ側は AppSettings を持たないので、読み込みと保存の経路をここから渡す。
        TabViewModel.LoadVideoPreferences(_settings.VideoVolume, _settings.VideoMuted, _settings.VideoRate);
        TabViewModel.VideoPreferencesChanged = (volume, muted, rate) =>
        {
            _settings.VideoVolume = volume;
            _settings.VideoMuted = muted;
            _settings.VideoRate = rate;
            SettingsService.Save(_settings);
        };
        _showStatusBar = _settings.ShowStatusBar;
        // 左ペインの表示内容。旧バージョンは bool の SidebarShowTree だけを持っていたので、
        // 未設定（空文字）なら旧フラグから引き継ぐ。IconSet と同じく移行後は旧フィールドを寝かせる。
        _sidebarMode = Models.SidebarModes.Resolve(_settings.SidebarMode, _settings.SidebarShowTree);
        _settings.SidebarMode = _sidebarMode.ToString();
        _settings.SidebarShowTree = false;
        _sidebarTreeSyncActive = _settings.SidebarTreeSyncActive;
        if (_sidebarMode == Models.SidebarMode.Tree)
        {
            EnsureSidebarTree();
        }
        ApplyTheme(_settings.ThemePreference);

        // ライセンス状態の初期化（ローカルキャッシュで即決し、裏でオンライン再検証）
        LicenseService.Initialize();
        LicenseService.StateChanged += OnLicenseStateChanged;

        RefreshBookmarks();
        // 起動時はドライブ列挙（ブロックしうる I/O）をせず、フォールバックのクイックアクセスだけで即描画する。
        // ドライブと画像アイコンはウィンドウ表示直後の RefreshSidebarAsync（バックグラウンド）で埋まる。
        BuildSidebar(QuickAccessService.GetFallbackSnapshot(), [], [], ownsIcons: false);

        // 終了時に選択していたタブを次回も選択状態で復元するため、これから作るタブの中から該当するものを
        // 追いかける。NavigateToAsync は非同期のため、作成直後は CurrentPath がまだ反映されていない
        // （既定値のまま）。よってタブの実際の CurrentPath ではなく、AddTab に渡した「復元先パス」自体で
        // 照合する（同期的に確定しているため、非同期のタイミング競合を受けない）。
        TabViewModel? lastSelectedCandidate = null;

        if (_settings.PinnedSettingsTab)
        {
            var settingsTab = AddSettingsTab(pinned: true);
            if (_settings.LastSelectedTabIsSettings)
            {
                lastSelectedCandidate = settingsTab;
            }
        }

        // 前回の固定タブを復元してから通常タブを開く。
        // ここで Directory.Exists による存在確認はしない: コンストラクタはウィンドウ生成前に UI スレッドで
        // 同期実行されるため、切断中のネットワークパスに対する存在確認が起動全体をブロックする。
        // 削除済みのパスは各タブの NavigateToAsync（バックグラウンド）が PC 表示へフォールバックするため、
        // ここでは無条件に復元してよい。
        foreach (var path in _settings.PinnedPaths)
        {
            var tab = AddTab(path, pinned: true);
            if (!_settings.LastSelectedTabIsSettings && WindowsPathIdentity.Instance.Equals(path, _settings.LastSelectedTabPath))
            {
                lastSelectedCandidate = tab;
            }
        }

        // 「前回開いていたタブを復元」設定（Chrome 互換）
        var restored = false;
        if (_settings.RestoreAllTabs)
        {
            if (_settings.OpenSettingsTab && !_settings.PinnedSettingsTab)
            {
                var settingsTab = AddSettingsTab(pinned: false);
                SelectedTab = settingsTab;
                restored = true;
                if (_settings.LastSelectedTabIsSettings)
                {
                    lastSelectedCandidate = settingsTab;
                }
            }

            foreach (var path in _settings.OpenTabPaths)
            {
                var tab = AddTab(path, pinned: false);
                SelectedTab = tab;
                restored = true;
                if (!_settings.LastSelectedTabIsSettings && WindowsPathIdentity.Instance.Equals(path, _settings.LastSelectedTabPath))
                {
                    lastSelectedCandidate = tab;
                }
            }
        }

        // コマンドライン引数のフォルダーを開く（kiriha.exe C:\path）
        var openedFromArgs = OpenShellPaths(Program.StartupArgs);
        restored |= openedFromArgs;

        if (!restored)
        {
            NewTab();
        }

        // シェル引数からの起動でなければ、終了時に選択していたタブを次回も選択状態で復元する
        // （固定タブは RestoreAllTabs 設定に関わらず常に復元されるため対象になりうる）。
        if (!openedFromArgs && lastSelectedCandidate is not null)
        {
            SelectedTab = lastSelectedCandidate;
        }

    }

    // ===== ライセンス（署名付きキー + 買い切り） =====

    [ObservableProperty]
    private string _licenseKeyInput = "";

    /// <summary>ライセンス欄に表示する案内・エラーメッセージ。</summary>
    [ObservableProperty]
    private string _licenseMessage = "";

    public string LicenseStatusText => LicenseService.State switch
    {
        LicenseState.Licensed => LocalizationService.Text("Text.License.Status.Licensed", LicenseService.Email),
        LicenseState.Trial => LocalizationService.Text("Text.License.Status.Trial", LicenseService.TrialDaysLeft),
        LicenseState.OnlineCheckRequired => LocalizationService.Text("Text.License.Status.OnlineCheck"),
        _ => LocalizationService.Text("Text.License.Status.TrialExpired"),
    };

    /// <summary>ロック画面に出す見出し（状態により文言を変える）。</summary>
    public string LicenseLockTitle => LicenseService.State == LicenseState.OnlineCheckRequired
        ? LocalizationService.Text("Text.License.Lock.OnlineCheckTitle")
        : LocalizationService.Text("Text.License.Lock.TrialExpiredTitle");

    public string LicenseLockDescription => LicenseService.State == LicenseState.OnlineCheckRequired
        ? LocalizationService.Text("Text.License.Lock.OnlineCheckBody")
        : LocalizationService.Text("Text.License.Lock.TrialExpiredBody");

    /// <summary>オンライン再確認ボタンの表示（猶予超過ロック時のみ）。</summary>
    public bool IsOnlineCheckRequired => LicenseService.State == LicenseState.OnlineCheckRequired;

    /// <summary>試用期限切れ・猶予超過による全画面ロック（認証 / 再確認で即解除）。</summary>
    public bool IsLicenseLocked
        => LicenseService.State is LicenseState.TrialExpired or LicenseState.OnlineCheckRequired;

    /// <summary>認証済みでない（購入導線・キー入力欄を表示する状態）。</summary>
    public bool IsNotLicensed => LicenseService.State != LicenseState.Licensed;

    /// <summary>認証済み（解除ボタンを表示する状態）。</summary>
    public bool IsLicensed => LicenseService.State == LicenseState.Licensed;

    private void OnLicenseStateChanged()
    {
        OnPropertyChanged(nameof(LicenseStatusText));
        OnPropertyChanged(nameof(LicenseLockTitle));
        OnPropertyChanged(nameof(LicenseLockDescription));
        OnPropertyChanged(nameof(IsOnlineCheckRequired));
        OnPropertyChanged(nameof(IsLicenseLocked));
        OnPropertyChanged(nameof(IsNotLicensed));
        OnPropertyChanged(nameof(IsLicensed));
    }

    /// <summary>この PC の認証を解除して未認証へ戻す（購入は有効なまま。動作確認用）。</summary>
    [RelayCommand]
    private void DeactivateLicense()
    {
        LicenseService.Deactivate();
        // 解除直後に前回の入力が残っていると紛らわしいので、認証欄を初期状態へ戻す。
        LicenseEmailInput = "";
        LicenseCodeInput = "";
        IsLicenseCodeSent = false;
        IsLicenseKeyEntryVisible = false;
        LicenseMessage = LocalizationService.Text("Text.License.Msg.Deactivated");
        OnLicenseStateChanged();
    }

    /// <summary>入力されたライセンスキーを検証して有効化する（オフラインで完結）。</summary>
    [RelayCommand]
    private void ActivateLicense()
    {
        if (LicenseKeyInput.Trim().Length == 0)
        {
            LicenseMessage = LocalizationService.Text("Text.License.Msg.EnterKey");
            return;
        }

        if (LicenseService.ActivateKey(LicenseKeyInput))
        {
            LicenseKeyInput = "";
            LicenseMessage = LocalizationService.Text("Text.License.Msg.Activated");
        }
        else
        {
            LicenseMessage = LocalizationService.Text("Text.License.Msg.InvalidKey");
        }

        OnLicenseStateChanged();
    }

    // ===== メールアドレス + 確認コードでの認証 =====
    //
    // 既定の導線はこちら。ライセンスキーは決済完了ページに 1 度出るだけで、別の PC では
    // 手元に無いことがほとんどなので、購入時のメールアドレスから復元できるようにする。
    // キーの直接入力は「キーを持っている人」「オフライン環境」のために残す。

    [ObservableProperty]
    private string _licenseEmailInput = "";

    [ObservableProperty]
    private string _licenseCodeInput = "";

    /// <summary>確認コードを送信済み（＝コード入力欄を出す）。</summary>
    [ObservableProperty]
    private bool _isLicenseCodeSent;

    /// <summary>hub と通信中（ボタンの二度押しを防ぐ）。</summary>
    [ObservableProperty]
    private bool _isLicenseBusy;

    /// <summary>ライセンスキーの直接入力欄を出しているか。既定はメール認証なので畳んでおく。</summary>
    [ObservableProperty]
    private bool _isLicenseKeyEntryVisible;

    [RelayCommand]
    private void ToggleLicenseKeyEntry() => IsLicenseKeyEntryVisible = !IsLicenseKeyEntryVisible;

    /// <summary>購入時のメールアドレスへ確認コードを送る。</summary>
    [RelayCommand]
    private async Task SendLicenseCodeAsync()
    {
        if (LicenseEmailInput.Trim().Length == 0)
        {
            LicenseMessage = LocalizationService.Text("Text.License.Msg.EnterEmail");
            return;
        }

        IsLicenseBusy = true;
        LicenseMessage = LocalizationService.Text("Text.License.Msg.Sending");
        var result = await LicenseService.RequestRecoveryCodeAsync(LicenseEmailInput);
        IsLicenseBusy = false;

        if (result == LicenseService.RecoveryRequestResult.Sent)
        {
            // 送信済みでもアドレスを直したくなることはあるので、入力欄は消さず残す。
            IsLicenseCodeSent = true;
        }

        LicenseMessage = LocalizationService.Text(result switch
        {
            LicenseService.RecoveryRequestResult.Sent => "Text.License.Msg.CodeSent",
            LicenseService.RecoveryRequestResult.InvalidEmail => "Text.License.Msg.EnterEmail",
            LicenseService.RecoveryRequestResult.TooSoon => "Text.License.Msg.CodeTooSoon",
            _ => "Text.License.Msg.ServerUnreachable",
        });
    }

    /// <summary>メールで届いた確認コードで認証する。</summary>
    [RelayCommand]
    private async Task RedeemLicenseCodeAsync()
    {
        if (LicenseCodeInput.Trim().Length == 0)
        {
            LicenseMessage = LocalizationService.Text("Text.License.Msg.EnterCode");
            return;
        }

        IsLicenseBusy = true;
        LicenseMessage = LocalizationService.Text("Text.License.Msg.Verifying");
        var result = await LicenseService.RedeemRecoveryCodeAsync(LicenseEmailInput, LicenseCodeInput);
        IsLicenseBusy = false;

        if (result == LicenseService.RecoveryRedeemResult.Activated)
        {
            LicenseEmailInput = "";
            LicenseCodeInput = "";
            IsLicenseCodeSent = false;
        }
        else
        {
            // 使い切ったコードは二度と通らないので、入力欄を空けて送り直しへ誘導する。
            LicenseCodeInput = "";
            IsLicenseCodeSent = result != LicenseService.RecoveryRedeemResult.InvalidCode;
        }

        LicenseMessage = LocalizationService.Text(result switch
        {
            LicenseService.RecoveryRedeemResult.Activated => "Text.License.Msg.Activated",
            LicenseService.RecoveryRedeemResult.InvalidCode => "Text.License.Msg.InvalidCode",
            LicenseService.RecoveryRedeemResult.NotPurchased => "Text.License.Msg.NoPurchase",
            _ => "Text.License.Msg.ServerUnreachable",
        });

        OnLicenseStateChanged();
    }

    /// <summary>オフライン猶予超過時のオンライン再確認。</summary>
    [RelayCommand]
    private async Task RecheckLicenseAsync()
    {
        LicenseMessage = LocalizationService.Text("Text.License.Msg.Checking");
        var ok = await LicenseService.CheckRevocationAsync();
        LicenseMessage = LicenseService.State switch
        {
            LicenseState.Licensed => LocalizationService.Text("Text.License.Msg.CheckOk"),
            _ when !ok => LocalizationService.Text("Text.License.Msg.Revoked"),
            _ => LocalizationService.Text("Text.License.Msg.ServerUnreachable"),
        };
        OnLicenseStateChanged();
    }

    [RelayCommand]
    private void OpenLicensePurchasePage()
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(LicenseService.PurchaseUrl) { UseShellExecute = true })?.Dispose();
            LicenseMessage = LocalizationService.Text("Text.License.Msg.PurchaseOpened");
        }
        catch (Exception ex)
        {
            Logger.LogException("購入ページを開けませんでした", ex);
            LicenseMessage = LocalizationService.Text("Text.License.Msg.PurchaseFailed");
        }
    }

    /// <summary>新しいタブの既定フォルダー（設定 > 起動フォルダー、無効ならユーザーフォルダー）。</summary>
    private string NewTabPath
        => _settings.StartupPath is { Length: > 0 } p && Directory.Exists(p)
            ? p
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [RelayCommand]
    private void NewTab()
    {
        var tab = AddTab(NewTabPath, pinned: false);
        SelectedTab = tab;
    }

    private TabViewModel AddTab(string path, bool pinned)
    {
        var initialViewSettings = _folderViewSettings.TryGet(path, out var savedViewSettings)
            ? savedViewSettings
            : CreateDefaultFolderViewSettings(path);
        var tab = new TabViewModel(path, Options, _folderViewSettings, initialViewSettings,
            defaultViewSettings: () => CreateDefaultFolderViewSettings(""))
        {
            ColNameWidth = _settings.ColNameWidth,
            ColModifiedWidth = _settings.ColModifiedWidth,
            ColCreatedWidth = _settings.ColCreatedWidth,
            ColTypeWidth = _settings.ColTypeWidth,
            ColSizeWidth = _settings.ColSizeWidth,
            ShowColModified = _settings.ShowColModified,
            ShowColCreated = _settings.ShowColCreated,
            ShowColType = _settings.ShowColType,
            ShowColSize = _settings.ShowColSize,
            IsCompactView = _settings.CompactView,
        };

        // 列幅はオブジェクト初期化子（＝コンストラクター実行後）で全体既定を入れているため、
        // フォルダーごとに覚えた幅はここで上書きし直す。順序を入れ替えると記憶した幅が消える。
        tab.ApplyFolderViewSettings(initialViewSettings);

        Tabs.Add(tab);
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.PinnedNavigationRequested += OpenPinnedNavigationInNewTab;
        tab.PropertyChanged += Tab_PropertyChanged;
        tab.IsPinned = pinned;
        tab.SetPreviewEnabled(ShowPreviewPane);
        return tab;
    }

    /// <summary>設定画面で決めた「フォルダーの既定表示」。表示設定を保存していないフォルダーで使う。
    /// 列幅は設定画面に項目が無いので、直近の幅（AppSettings の Col*Width）を全体の既定として載せる。</summary>
    private FolderViewSettings CreateDefaultFolderViewSettings(string path)
        => new()
        {
            Path = path,
            ViewMode = _settings.DefaultViewMode,
            IconSize = _settings.DefaultIconSize,
            SortKey = _settings.DefaultSortKey,
            SortAscending = _settings.DefaultSortAscending,
            ColumnWidths = new Dictionary<string, double>
            {
                [SortKeys.Name] = _settings.ColNameWidth,
                [SortKeys.Modified] = _settings.ColModifiedWidth,
                [SortKeys.Created] = _settings.ColCreatedWidth,
                [SortKeys.Type] = _settings.ColTypeWidth,
                [SortKeys.Size] = _settings.ColSizeWidth,
            },
        };

    private TabViewModel AddSettingsTab(bool pinned)
    {
        var tab = new TabViewModel(FileSystemService.ComputerPath, Options, isSettingsTab: true)
        {
            IsPinned = pinned,
        };
        Tabs.Add(tab);
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.PropertyChanged += Tab_PropertyChanged;
        tab.SetPreviewEnabled(false);
        return tab;
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabViewModel tab)
        {
            return;
        }

        if (e.PropertyName == nameof(TabViewModel.IsGalleryView))
        {
            // ギャラリーを抜けたら（Esc・✕・スライダー・ホイールのいずれでも）全画面も一緒に解除する
            if (ReferenceEquals(tab, SelectedTab))
            {
                if (!tab.IsGalleryView)
                {
                    IsGalleryFullScreen = false;
                }

                // 最大化中はギャラリーの出入りで余白の有無が変わる
                NotifyGalleryEdgeToEdge();
            }
        }
        else if (e.PropertyName == nameof(TabViewModel.IsPinned))
        {
            ReorderPinned(tab);
            SavePinned();
        }
        else if (e.PropertyName == nameof(TabViewModel.Title) && ReferenceEquals(tab, SelectedTab))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
        else if (e.PropertyName == nameof(TabViewModel.IconSize))
        {
            if (tab.IsApplyingFolderViewSettings) return;
            // アイコンの大きさだけは設定画面に項目が無いので、最後に使った値を既定として引き継ぐ。
            // マウスホイールで連続発火するため、Col*Width と同様に即時保存はせず終了時にまとめて保存する。
            // 表示方法と並べ替えの既定は設定画面で明示的に決めるものなので、ここでは追従させない
            //（追従させると、設定した既定が操作のたびに勝手に書き換わってしまう）。
            _settings.DefaultIconSize = tab.IconSize;
        }
        else if (e.PropertyName is nameof(TabViewModel.ColNameWidth) or nameof(TabViewModel.ColModifiedWidth)
                 or nameof(TabViewModel.ColCreatedWidth) or nameof(TabViewModel.ColTypeWidth) or nameof(TabViewModel.ColSizeWidth))
        {
            // Thumb ドラッグ中に高頻度で発火するため、SidebarWidth と同様に即時保存はせず終了時にまとめて保存する
            _settings.ColNameWidth = tab.ColNameWidth;
            _settings.ColModifiedWidth = tab.ColModifiedWidth;
            _settings.ColCreatedWidth = tab.ColCreatedWidth;
            _settings.ColTypeWidth = tab.ColTypeWidth;
            _settings.ColSizeWidth = tab.ColSizeWidth;
        }
        else if (e.PropertyName == nameof(TabViewModel.CurrentPath))
        {
            // 「アクティブ ドキュメントとの同期」がオンのときだけ、選択中タブのフォルダー移動へ
            // ツリービューの展開・選択を追従させる
            if (SidebarTreeSyncActive && ReferenceEquals(tab, SelectedTab))
            {
                _ = SyncSidebarTreeToCurrentPathAsync();
            }
        }
        else if (e.PropertyName == nameof(TabViewModel.IsCompactView))
        {
            // コンパクトビューはアプリ一律の設定。どのタブで切り替えても全タブへ反映し、
            // 次回起動時の状態として保存する（伝播中の再入は無視）。
            if (!_isPropagatingCompactView)
            {
                _isPropagatingCompactView = true;
                try
                {
                    foreach (var t in Tabs)
                    {
                        t.IsCompactView = tab.IsCompactView;
                    }
                }
                finally
                {
                    _isPropagatingCompactView = false;
                }

                _settings.CompactView = tab.IsCompactView;
                SettingsService.Save(_settings);
            }
        }
        else if (e.PropertyName is nameof(TabViewModel.ShowColModified) or nameof(TabViewModel.ShowColCreated)
                 or nameof(TabViewModel.ShowColType) or nameof(TabViewModel.ShowColSize))
        {
            _settings.ShowColModified = tab.ShowColModified;
            _settings.ShowColCreated = tab.ShowColCreated;
            _settings.ShowColType = tab.ShowColType;
            _settings.ShowColSize = tab.ShowColSize;
            SettingsService.Save(_settings);
        }
    }

    /// <summary>Chrome と同じく、固定タブは左端の固定ブロックへ移動する。</summary>
    private void ReorderPinned(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var target = Tabs.Count(t => t != tab && t.IsPinned);
        if (index != target)
        {
            MoveTabPreservingSelection(index, target);
        }
    }

    /// <summary>ObservableCollection.Move の通知を ListBox の SelectedItem 双方向バインディングが
    /// 正しく維持しないことがあり、並べ替えで SelectedTab が null になる。null になると
    /// SelectedTab を参照する IsVisible バインディングが軒並み既定値（表示）へ戻り、
    /// タブ一覧とギャラリーの Exif パネルが同時に出るなど表示が壊れるため、移動後に明示的に復元する。</summary>
    private void MoveTabPreservingSelection(int from, int to)
    {
        var previousSelection = SelectedTab;
        _isMovingTab = true;
        try
        {
            Tabs.Move(from, to);
            if (!ReferenceEquals(SelectedTab, previousSelection))
            {
                SelectedTab = previousSelection;
            }
        }
        finally
        {
            _isMovingTab = false;
        }
    }

    /// <summary>MoveTabPreservingSelection 実行中の印（OnSelectedTabChanged の副作用抑止用）。</summary>
    private bool _isMovingTab;

    private void SavePinned()
    {
        _settings.PinnedPaths = Tabs.Where(t => t.IsPinned && !t.IsSettingsTab).Select(t => t.CurrentPath).ToList();
        _settings.PinnedSettingsTab = Tabs.Any(t => t.IsSettingsTab && t.IsPinned);
        SettingsService.Save(_settings);
    }

    [RelayCommand]
    private void CloseTab(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        tab.Detach();
        tab.PinnedNavigationRequested -= OpenPinnedNavigationInNewTab;
        tab.PropertyChanged -= Tab_PropertyChanged;
        if (!tab.IsSettingsTab)
        {
            _closedTabPaths.Push(tab.CurrentPath);
        }

        Tabs.RemoveAt(index);
        if (tab.IsPinned)
        {
            SavePinned();
        }

        // Chrome と同じく最後の 1 タブを閉じたら新しいタブで維持する（ウィンドウは残す）
        if (Tabs.Count == 0)
        {
            NewTab();
            return;
        }

        if (SelectedTab is null || !Tabs.Contains(SelectedTab))
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    // ===== お気に入り =====

    public bool HasNoBookmarks => BookmarkItems.Count == 0;

    /// <summary>settings の Bookmarks から表示用コレクションを再構築する。</summary>
    public void RefreshBookmarks()
    {
        BookmarkItems.Clear();
        foreach (var node in _settings.Bookmarks)
        {
            BookmarkItems.Add(node);
        }

        OnPropertyChanged(nameof(HasNoBookmarks));
        _ = RefreshBookmarkIconsAsync();
    }

    /// <summary>お気に入りの表示アイコンを、いまのアイコンセット設定で付け直す。
    /// 実体がフォルダーかファイルかの判定（切断中のネットワークパスでブロックしうる）と
    /// シェルアイコンの取得はバックグラウンドで行い、結果だけを各ノードへ書き戻す。</summary>
    public async Task RefreshBookmarkIconsAsync()
    {
        var generation = ++_bookmarkIconGeneration;
        var nodes = new List<BookmarkNode>();
        CollectBookmarkLinks(_settings.Bookmarks, nodes);
        if (nodes.Count == 0)
        {
            return;
        }

        var iconSet = Options.IconSet;
        var preferLight = iconSet == FileIconSet.Material && MaterialIconService.IsLightTheme();
        var paths = nodes.Select(n => n.Path!).ToList();
        var resolved = await Task.Run(() => ResolveBookmarkIcons(paths, iconSet, preferLight));

        // 並走した新しい呼び出しがある場合、こちらの（古い）結果は適用せず破棄する
        if (generation != _bookmarkIconGeneration)
        {
            if (iconSet == FileIconSet.Windows)
            {
                foreach (var entry in resolved)
                {
                    if (entry.Image is Avalonia.Media.Imaging.Bitmap stale) stale.Dispose();
                }
            }

            return;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var (icon, image, isDirectory) = resolved[i];
            nodes[i].SetIcon(icon, image, ownsImage: iconSet == FileIconSet.Windows, isDirectory);
        }
    }

    /// <summary>連続呼び出し時に古い結果が新しい結果を上書きしないための世代番号。</summary>
    private int _bookmarkIconGeneration;

    /// <summary>お気に入りのうちリンク（パスを持つ項目）だけを、ツリー順に集める。</summary>
    private static void CollectBookmarkLinks(List<BookmarkNode> nodes, List<BookmarkNode> into)
    {
        foreach (var node in nodes)
        {
            if (node.Children is { } children)
            {
                CollectBookmarkLinks(children, into);
            }
            else if (node.Path is { Length: > 0 })
            {
                into.Add(node);
            }
        }
    }

    /// <summary>UI スレッド外で実行する部分。パスごとに（絵文字, 画像アイコン, フォルダーか）を返す。</summary>
    private static List<(string Icon, Avalonia.Media.IImage? Image, bool IsDirectory)> ResolveBookmarkIcons(
        List<string> paths,
        FileIconSet iconSet,
        bool preferLight)
    {
        var result = new List<(string, Avalonia.Media.IImage?, bool)>(paths.Count);
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path;
            // 実体が消えていても列挙は続ける（お気に入りは実体より長生きしうる）。
            // その場合はファイル扱いの既定アイコンになる。
            var isDirectory = SafeIsDirectory(path);
            var icon = FileSystemEntry.ResolveEmojiIcon(name, isDirectory);
            Avalonia.Media.IImage? image = iconSet switch
            {
                FileIconSet.Windows => ShellThumbnailService.TryGetIcon(path, 32),
                FileIconSet.Material => MaterialIconService.ResolveIconKey(name, isDirectory, preferLight) is { Length: > 0 } key
                    ? MaterialIconService.GetImage(key)
                    : null,
                _ => null,
            };
            result.Add((icon, image, isDirectory));
        }

        return result;
    }

    private static bool SafeIsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex)
        {
            Logger.LogException($"お気に入りの実体を判定できませんでした: {path}", ex);
            return false;
        }
    }

    public void SaveBookmarks()
    {
        SettingsService.Save(_settings);
        RefreshBookmarks();
    }

    /// <summary>お気に入りへ追加（parent が null ならルート）。</summary>
    public void AddBookmark(string path, BookmarkNode? parent = null)
    {
        var name = path == FileSystemService.ComputerPath
            ? "PC"
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path;
        var target = parent?.Children ?? _settings.Bookmarks;
        if (target.Any(b => b.Path is not null && WindowsPathIdentity.Instance.Equals(b.Path, path)))
        {
            return;
        }

        target.Add(new BookmarkNode { Name = name, Path = path });
        SaveBookmarks();
    }

    /// <summary>
    /// ドラッグ＆ドロップで示された位置へお気に入りを挿入する。
    /// reference が null（項目の無い余白へのドロップ）なら末尾へ足す。
    /// 既に同じパスが登録されているときは、その項目を指定位置へ移動する
    /// （追加できずに何も起きないと、ユーザーからは並べ替えに失敗したように見えるため）。
    /// </summary>
    public void InsertBookmark(string path, BookmarkNode? reference, BookmarkDropMark mark)
    {
        var (target, index) = ResolveBookmarkInsertion(reference, mark);
        var name = path == FileSystemService.ComputerPath
            ? "PC"
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path;

        if (FindBookmarkByPath(_settings.Bookmarks, path) is { } existing)
        {
            // 元の位置を抜く前に挿入位置を求めてあるので、同じリスト内で前を抜いた分だけ詰める
            var sourceIndex = target.IndexOf(existing);
            if (sourceIndex >= 0 && sourceIndex < index)
            {
                index--;
            }

            RemoveBookmarkRecursive(_settings.Bookmarks, existing);
            target.Insert(Math.Clamp(index, 0, target.Count), existing);
            SaveBookmarks();
            return;
        }

        target.Insert(Math.Clamp(index, 0, target.Count), new BookmarkNode { Name = name, Path = path });
        SaveBookmarks();
    }

    /// <summary>目印の付いた項目から「どのリストの何番目か」を求める。</summary>
    private (List<BookmarkNode> Target, int Index) ResolveBookmarkInsertion(BookmarkNode? reference, BookmarkDropMark mark)
    {
        if (reference is null || mark == BookmarkDropMark.None)
        {
            return (_settings.Bookmarks, _settings.Bookmarks.Count);
        }

        if (mark == BookmarkDropMark.Into && reference.Children is { } children)
        {
            return (children, children.Count);
        }

        var parent = FindBookmarkParent(_settings.Bookmarks, reference) ?? _settings.Bookmarks;
        var index = parent.IndexOf(reference);
        if (index < 0)
        {
            return (_settings.Bookmarks, _settings.Bookmarks.Count);
        }

        return (parent, mark == BookmarkDropMark.After ? index + 1 : index);
    }

    private static List<BookmarkNode>? FindBookmarkParent(List<BookmarkNode> list, BookmarkNode node)
    {
        if (list.Contains(node))
        {
            return list;
        }

        foreach (var child in list)
        {
            if (child.Children is { } children && FindBookmarkParent(children, node) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static BookmarkNode? FindBookmarkByPath(List<BookmarkNode> list, string path)
    {
        foreach (var node in list)
        {
            if (node.Children is { } children)
            {
                if (FindBookmarkByPath(children, path) is { } found)
                {
                    return found;
                }
            }
            else if (node.Path is { Length: > 0 } target && WindowsPathIdentity.Instance.Equals(target, path))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>登録済みのお気に入りから、そのパスの項目を探す（連続ドロップの基準点に使う）。</summary>
    public BookmarkNode? FindBookmark(string path) => FindBookmarkByPath(_settings.Bookmarks, path);

    /// <summary>ドラッグ終了時に、全項目の挿入位置の目印を消す。</summary>
    public void ClearBookmarkDropMarks() => ClearBookmarkDropMarks(_settings.Bookmarks);

    private static void ClearBookmarkDropMarks(List<BookmarkNode> list)
    {
        foreach (var node in list)
        {
            node.DropMark = BookmarkDropMark.None;
            if (node.Children is { } children)
            {
                ClearBookmarkDropMarks(children);
            }
        }
    }

    /// <summary>指定の項目だけに目印を付け、他は消す。</summary>
    public void SetBookmarkDropMark(BookmarkNode? node, BookmarkDropMark mark)
    {
        ClearBookmarkDropMarks();
        if (node is not null)
        {
            node.DropMark = mark;
        }
    }

    public void AddBookmarkFolder(string name, BookmarkNode? parent = null)
    {
        var target = parent?.Children ?? _settings.Bookmarks;
        target.Add(new BookmarkNode { Name = name, Children = new List<BookmarkNode>() });
        SaveBookmarks();
    }

    public void RemoveBookmark(BookmarkNode node)
    {
        RemoveBookmarkRecursive(_settings.Bookmarks, node);
        SaveBookmarks();
    }

    private static bool RemoveBookmarkRecursive(List<BookmarkNode> list, BookmarkNode node)
    {
        if (list.Remove(node))
        {
            return true;
        }

        return list.Any(child => child.Children is not null && RemoveBookmarkRecursive(child.Children, node));
    }

    public void RenameBookmark(BookmarkNode node, string newName)
    {
        if (!string.IsNullOrWhiteSpace(newName))
        {
            node.Name = newName;
            SaveBookmarks();
        }
    }

    /// <summary>Chrome の「名前順で並べ替え / パス名順で並べ替え」（フォルダー優先、ネスト内も再帰的に）。</summary>
    public void SortBookmarks(bool byPath)
    {
        _settings.Bookmarks = SortBookmarkList(_settings.Bookmarks, byPath);
        SaveBookmarks();
    }

    private static List<BookmarkNode> SortBookmarkList(List<BookmarkNode> list, bool byPath)
    {
        foreach (var folder in list.Where(b => b.Children is not null))
        {
            folder.Children = SortBookmarkList(folder.Children!, byPath);
        }

        return list
            .OrderByDescending(b => b.IsFolder)
            .ThenBy(b => byPath ? b.Path ?? "" : b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>ウィンドウの位置・サイズ・最大化状態・開いていたタブの保存（終了時に呼ばれる）。</summary>
    public void SaveWindowBounds(
        double width,
        double height,
        int x,
        int y,
        bool maximized,
        (int X, int Y, int Width, int Height)? monitorWorkingArea)
    {
        // 「ウィンドウのサイズと位置を保存する」が OFF のときはウィンドウ枠の情報は書き換えず、
        // 開いていたタブなどセッション情報だけを保存する。
        if (_settings.RememberWindowBounds)
        {
            if (!maximized)
            {
                _settings.WindowWidth = width;
                _settings.WindowHeight = height;
                _settings.WindowX = x;
                _settings.WindowY = y;
            }

            _settings.WindowMaximized = maximized;
            SaveWindowMonitor(monitorWorkingArea);
        }

        SaveOpenTabsAndSettings();
    }

    /// <summary>最小化中に閉じられた場合の保存（Win32 は最小化中の座標をセンチネル値 (-32000,-32000)
    /// で返すため、位置・サイズは前回保存済みの値を維持し最大化フラグだけ更新する）。</summary>
    public void SaveWindowMaximizedFlag(
        bool maximized,
        (int X, int Y, int Width, int Height)? monitorWorkingArea)
    {
        if (_settings.RememberWindowBounds)
        {
            _settings.WindowMaximized = maximized;
            SaveWindowMonitor(monitorWorkingArea);
        }

        SaveOpenTabsAndSettings();
    }

    private void SaveWindowMonitor((int X, int Y, int Width, int Height)? monitorWorkingArea)
    {
        if (monitorWorkingArea is not { Width: > 0, Height: > 0 } monitor)
        {
            return;
        }

        _settings.WindowMonitorX = monitor.X;
        _settings.WindowMonitorY = monitor.Y;
        _settings.WindowMonitorWidth = monitor.Width;
        _settings.WindowMonitorHeight = monitor.Height;
    }

    /// <summary>プロセス終了時に呼ぶ。保留中の記憶を書き切って遅延保存タイマーを止める。</summary>
    public void Shutdown()
    {
        try
        {
            _folderViewSettings.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogException("終了処理でフォルダー別表示設定を保存できませんでした", ex);
        }
    }

    private void SaveOpenTabsAndSettings()
    {
        _settings.OpenTabPaths = Tabs
            .Where(t => !t.IsSettingsTab && !t.IsPinned)
            .Select(t => t.CurrentPath)
            .ToList();
        _settings.OpenSettingsTab = Tabs.Any(t => t.IsSettingsTab && !t.IsPinned);

        _settings.LastSelectedTabIsSettings = SelectedTab?.IsSettingsTab ?? false;
        _settings.LastSelectedTabPath = SelectedTab is { IsSettingsTab: false } selected ? selected.CurrentPath : "";

        _folderViewSettings.Flush();
        SettingsService.Save(_settings);
    }

    /// <summary>前回のウィンドウサイズ（未保存なら null）。</summary>
    public (double Width, double Height)? SavedWindowSize
        => _settings.WindowWidth > 200 && _settings.WindowHeight > 200
            ? (_settings.WindowWidth, _settings.WindowHeight)
            : null;

    /// <summary>前回のウィンドウ位置（未保存なら null）。</summary>
    public (int X, int Y)? SavedWindowPosition
        => _settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue
            ? (_settings.WindowX, _settings.WindowY)
            : null;

    public bool SavedWindowMaximized => _settings.WindowMaximized;

    /// <summary>終了時にウィンドウが表示されていたモニターの作業領域。</summary>
    public (int X, int Y, int Width, int Height)? SavedWindowMonitorWorkingArea
        => _settings.WindowMonitorX != int.MinValue
           && _settings.WindowMonitorY != int.MinValue
           && _settings.WindowMonitorWidth > 0
           && _settings.WindowMonitorHeight > 0
            ? (_settings.WindowMonitorX, _settings.WindowMonitorY,
                _settings.WindowMonitorWidth, _settings.WindowMonitorHeight)
            : null;

    // ===== タブ操作（Chrome 互換） =====

    /// <summary>指定パスを新しいタブで開く（サイドバー / お気に入りの中クリックなど）。</summary>
    public void OpenInNewTab(string path)
    {
        var tab = AddTab(path, pinned: false);
        SelectedTab = tab;
    }

    /// <summary>固定タブの階層を維持したまま、移動先を選択状態の通常タブとして開く。</summary>
    private void OpenPinnedNavigationInNewTab(string path) => OpenInNewTab(path);

    /// <summary>バックグラウンドの新しいタブで開く（Chrome の中クリックと同じく選択を移さない）。</summary>
    public void OpenInNewTabBackground(string path) => AddTab(path, pinned: false);

    /// <summary>最近閉じたタブの一覧（新しい順、最大 10 件）。</summary>
    public IReadOnlyList<string> ClosedTabPaths => _closedTabPaths.Take(10).ToList();

    /// <summary>「最近閉じたタブ」メニューから特定のパスを開き直す。</summary>
    public void ReopenClosedPath(string path)
    {
        var list = _closedTabPaths.ToList();
        if (list.Remove(path))
        {
            _closedTabPaths.Clear();
            for (var i = list.Count - 1; i >= 0; i--)
            {
                _closedTabPaths.Push(list[i]);
            }
        }

        if (path == FileSystemService.ComputerPath || Directory.Exists(path))
        {
            OpenInNewTab(path);
        }
    }

    /// <summary>タブを上下に 1 つ移動する（Ctrl+Shift+PgUp/PgDn）。</summary>
    public void MoveSelectedTab(int direction)
    {
        if (SelectedTab is { } tab)
        {
            MoveTab(tab, Tabs.IndexOf(tab) + direction);
        }
    }

    /// <summary>ウィンドウのアクティブ化で全タブの貼り付け活性を再評価する。</summary>
    public void NotifyClipboardChanged()
    {
        foreach (var tab in Tabs)
        {
            tab.NotifyClipboardChanged();
        }
    }

    /// <summary>「新しいタブを下に開く」。</summary>
    public void NewTabToRight(TabViewModel anchor)
    {
        var tab = AddTab(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pinned: false);
        var index = Tabs.IndexOf(anchor);
        Tabs.Move(Tabs.IndexOf(tab), Math.Min(Math.Max(index + 1, Tabs.Count(t => t.IsPinned)), Tabs.Count - 1));
        SelectedTab = tab;
    }

    /// <summary>次 / 前のタブへ切り替える（Ctrl+Tab / Ctrl+Shift+Tab）。</summary>
    public void SelectAdjacentTab(int direction)
    {
        if (Tabs.Count == 0 || SelectedTab is null)
        {
            return;
        }

        var index = (Tabs.IndexOf(SelectedTab) + direction + Tabs.Count) % Tabs.Count;
        SelectedTab = Tabs[index];
    }

    /// <summary>タブを複製する。</summary>
    public void DuplicateTab(TabViewModel source)
    {
        if (source.IsSettingsTab)
        {
            return;
        }

        var tab = AddTab(source.CurrentPath, pinned: false);
        var index = Tabs.IndexOf(source);
        Tabs.Move(Tabs.IndexOf(tab), Math.Min(index + 1, Tabs.Count - 1));
        SelectedTab = tab;
    }

    /// <summary>下側のタブを閉じる（固定タブは残す）。</summary>
    public void CloseTabsToRight(TabViewModel anchor)
    {
        var index = Tabs.IndexOf(anchor);
        foreach (var tab in Tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList())
        {
            CloseTab(tab);
        }
    }

    /// <summary>他のタブを閉じる（固定タブは残す）。</summary>
    public void CloseOtherTabs(TabViewModel keep)
    {
        foreach (var tab in Tabs.Where(t => t != keep && !t.IsPinned).ToList())
        {
            CloseTab(tab);
        }

        SelectedTab = keep;
    }

    /// <summary>タブのドラッグ並べ替え（固定 / 非固定の境界を越えない）。</summary>
    public void MoveTab(TabViewModel tab, int targetIndex)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0 || targetIndex < 0 || targetIndex >= Tabs.Count || from == targetIndex)
        {
            return;
        }

        var pinnedCount = Tabs.Count(t => t.IsPinned);
        if (tab.IsPinned && targetIndex >= pinnedCount)
        {
            targetIndex = pinnedCount - 1;
        }
        else if (!tab.IsPinned && targetIndex < pinnedCount)
        {
            targetIndex = pinnedCount;
        }

        if (from != targetIndex)
        {
            MoveTabPreservingSelection(from, targetIndex);

            if (tab.IsPinned)
            {
                SavePinned();
            }
        }
    }

    /// <summary>フォルダー群を指定位置へ新しいタブとして開いて選択する（タブバーへのドロップ用）。
    /// 固定タブブロックより前には挿入しない。</summary>
    public void OpenFolderTabsAt(IEnumerable<string> paths, int index)
    {
        var pinnedCount = Tabs.Count(t => t.IsPinned);
        index = Math.Clamp(index, pinnedCount, Tabs.Count);
        foreach (var path in paths.Where(Directory.Exists))
        {
            var tab = AddTab(Path.GetFullPath(path), pinned: false);
            MoveTab(tab, Math.Min(index, Tabs.Count - 1));
            SelectedTab = tab;
            index = Tabs.IndexOf(tab) + 1;
        }
    }

    /// <summary>選択したフォルダー群をまとめて末尾のタブとして開く（ファイル一覧の右クリック用）。
    /// 開いた順に並べ、最後の 1 つを選択状態にする。</summary>
    public void OpenFolderTabs(IEnumerable<string> paths)
    {
        TabViewModel? last = null;
        foreach (var path in paths.Where(Directory.Exists))
        {
            last = AddTab(Path.GetFullPath(path), pinned: false);
        }

        if (last is not null)
        {
            SelectedTab = last;
        }
    }

    /// <summary>選択したフォルダー群を固定タブとして追加する（ファイル一覧の右クリック用）。
    /// 固定タブは「そこに常駐させて後から参照する」ための置き場なので、開いても選択は移さない
    /// （今の作業タブから離れず、複数まとめて固定しても最後の 1 つへ飛ばされない）。
    /// AddTab の pinned 指定で IsPinned が立ち、Tab_PropertyChanged 経由で固定ブロックへの
    /// 並べ替えと設定の保存まで走る。</summary>
    public void PinFolderTabs(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(Directory.Exists))
        {
            AddTab(Path.GetFullPath(path), pinned: true);
        }
    }

    /// <summary>Explorer または二重起動から渡されたフォルダーを新しいタブで開く。</summary>
    public bool OpenShellPaths(IEnumerable<string> paths)
    {
        var opened = false;
        foreach (var path in paths.Where(Directory.Exists))
        {
            SelectedTab = AddTab(Path.GetFullPath(path), pinned: false);
            opened = true;
        }

        return opened;
    }

    /// <summary>タブ右クリックに追加した一括管理操作を実行する。</summary>
    public void ExecuteTabManagement(string actionId, TabViewModel anchor)
    {
        if (!Tabs.Contains(anchor)) return;
        var index = Tabs.IndexOf(anchor);
        var normal = Tabs.Where(t => !t.IsSettingsTab).ToList();
        switch (actionId)
        {
            case "tab.close-left":
                foreach (var tab in Tabs.Take(index).Where(t => !t.IsPinned).ToList()) CloseTab(tab);
                break;
            case "tab.close-duplicates":
                foreach (var group in normal.GroupBy(t => t.CurrentPath, WindowsPathIdentity.Instance))
                    foreach (var tab in group.Skip(1).Where(t => !t.IsPinned).ToList()) CloseTab(tab);
                break;
            case "tab.close-unpinned":
                foreach (var tab in normal.Where(t => !t.IsPinned).ToList()) CloseTab(tab);
                break;
            case "tab.pin-all": SetPinned(normal, true); break;
            case "tab.unpin-all": SetPinned(normal, false); break;
            case "tab.pin-left": SetPinned(Tabs.Take(index).Where(t => !t.IsSettingsTab).ToList(), true); break;
            case "tab.pin-right": SetPinned(Tabs.Skip(index + 1).Where(t => !t.IsSettingsTab).ToList(), true); break;
            case "tab.reload-all": RefreshTabs(normal); break;
            case "tab.reload-left": RefreshTabs(Tabs.Take(index)); break;
            case "tab.reload-right": RefreshTabs(Tabs.Skip(index + 1)); break;
            case "tab.move-first": MoveTab(anchor, anchor.IsPinned ? 0 : Tabs.Count(t => t.IsPinned)); break;
            case "tab.move-last": MoveTab(anchor, anchor.IsPinned ? Math.Max(0, Tabs.Count(t => t.IsPinned) - 1) : Tabs.Count - 1); break;
            case "tab.sort-title": ReorderTabs(normal.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)); break;
            case "tab.sort-path": ReorderTabs(normal.OrderBy(t => t.CurrentPath, StringComparer.OrdinalIgnoreCase)); break;
            case "tab.reverse": ReorderTabs(normal.AsEnumerable().Reverse()); break;
            case "tab.open-parent":
                if (anchor.CurrentPath != FileSystemService.ComputerPath && Directory.GetParent(anchor.CurrentPath) is { } parent)
                    OpenInNewTab(parent.FullName);
                break;
            default:
                // ContextActionCatalog の起動時検証はカタログ内部の整合性しか見ないため、
                // カタログ→ハンドラーの対応漏れ（ID 追加・リネーム時の直し忘れ）はここで検出する
                Logger.Log($"未対応のタブ操作 ID です: {actionId}", LogLevel.Warning);
                break;
        }
    }

    /// <summary>タブ一覧下部の並べ替えボタン用。固定タブ・通常タブそれぞれのブロック内で名前順に並べる。</summary>
    [RelayCommand]
    private void SortTabsByName()
        => ReorderTabs(Tabs.Where(t => !t.IsSettingsTab)
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    /// <summary>同じくパス順。同じ親フォルダーのタブが隣り合うので、階層で揃えたいときはこちら。</summary>
    [RelayCommand]
    private void SortTabsByPath()
        => ReorderTabs(Tabs.Where(t => !t.IsSettingsTab)
            .OrderBy(t => t.CurrentPath, StringComparer.OrdinalIgnoreCase)
            .ToList());

    /// <summary>並び順の反転。並べ替えた直後に押せば降順になる。</summary>
    [RelayCommand]
    private void ReverseTabOrder()
        => ReorderTabs(Tabs.Where(t => !t.IsSettingsTab).Reverse().ToList());

    private static void RefreshTabs(IEnumerable<TabViewModel> tabs)
    {
        foreach (var tab in tabs.Where(t => !t.IsSettingsTab).ToList()) tab.RefreshCommand.Execute(null);
    }

    private static void SetPinned(IEnumerable<TabViewModel> tabs, bool pinned)
    {
        foreach (var tab in tabs.ToList()) tab.IsPinned = pinned;
    }

    /// <summary>並べ替えの本体。固定タブと通常タブはそれぞれのブロック内だけで並び替え、
    /// ブロックの境界（固定タブが必ず上）は動かさない。
    /// 設定タブは並べ替えの対象外（パスも表示名の意味も他のタブと違う）だが、
    /// 固定されているなら固定ブロックの末尾に残す。ここを一律で全体の末尾へ送ると、
    /// 固定した設定タブが並べ替えのたびに固定ブロックから弾き出されてしまう。</summary>
    private void ReorderTabs(IEnumerable<TabViewModel> ordered)
    {
        var orderedList = ordered.ToList();
        var settingsTabs = Tabs.Where(t => t.IsSettingsTab).ToList();
        var pinned = orderedList.Where(t => t.IsPinned)
            .Concat(settingsTabs.Where(t => t.IsPinned))
            .ToList();
        var unpinned = orderedList.Where(t => !t.IsPinned)
            .Concat(settingsTabs.Where(t => !t.IsPinned))
            .ToList();
        var target = pinned.Concat(unpinned).ToList();
        for (var i = 0; i < target.Count; i++)
        {
            var current = Tabs.IndexOf(target[i]);
            if (current != i) MoveTabPreservingSelection(current, i);
        }
        SavePinned();
    }

    // ===== サイドバー =====

    /// <summary>クイックアクセスのピン留め変更後などに左ペインを再構築する。</summary>
    public void RefreshSidebar()
        => _ = RefreshSidebarAsync();

    /// <summary>連続呼び出し時に古い結果が新しい結果を上書きしないための世代番号。</summary>
    private int _sidebarRefreshGeneration;

    public async Task RefreshSidebarAsync()
    {
        // クイックアクセス列挙とドライブ列挙（DriveInfo.IsReady / 空き容量は切断中の
        // ネットワークドライブでブロックしうる）をまとめてバックグラウンドで取得してから再構築する。
        var generation = ++_sidebarRefreshGeneration;
        var iconSet = Options.IconSet;
        var preferLight = iconSet == FileIconSet.Material && MaterialIconService.IsLightTheme();
        var (snapshot, drives, icons) = await Task.Run(() =>
        {
            var snap = QuickAccessService.GetSnapshot();
            var driveList = GetDriveDisplays();
            return (snap, driveList, BuildSidebarIconImages(snap, driveList, iconSet, preferLight));
        });

        // 並走した新しい呼び出しがある場合、こちらの（古い）結果は適用せず破棄する
        if (generation != _sidebarRefreshGeneration)
        {
            foreach (var image in icons.Values)
            {
                if (iconSet == FileIconSet.Windows && image is Avalonia.Media.Imaging.Bitmap stale) stale.Dispose();
            }
            return;
        }

        // Windows Shell アイコンはこちらが所有しているため、旧項目の分を解放してから差し替える
        foreach (var link in SidebarItems.OfType<SidebarLink>())
        {
            if (link is { OwnsIconImage: true, IconImage: Avalonia.Media.Imaging.Bitmap bitmap }) bitmap.Dispose();
        }

        SidebarItems.Clear();
        BuildSidebar(snapshot, drives, icons, ownsIcons: iconSet == FileIconSet.Windows);
    }

    /// <summary>サイドバー項目のパス→画像アイコン対応表を、現在のアイコンセット設定で構築する。</summary>
    private static Dictionary<string, Avalonia.Media.IImage> BuildSidebarIconImages(
        QuickAccessService.Snapshot quickAccess,
        IReadOnlyList<DriveDisplay> drives,
        FileIconSet iconSet,
        bool preferLight)
    {
        var icons = new Dictionary<string, Avalonia.Media.IImage>(WindowsPathIdentity.Instance);
        if (iconSet == FileIconSet.Original)
        {
            return icons;
        }

        void Add(string path, string name, bool isDirectory)
        {
            if (path.Length == 0 || icons.ContainsKey(path))
            {
                return;
            }

            Avalonia.Media.IImage? image;
            if (iconSet == FileIconSet.Windows)
            {
                image = ShellThumbnailService.TryGetIcon(path, 32);
            }
            else
            {
                var key = MaterialIconService.ResolveIconKey(name, isDirectory, preferLight);
                image = key.Length > 0 ? MaterialIconService.GetImage(key) : null;
            }

            if (image is not null)
            {
                icons[path] = image;
            }
        }

        foreach (var (name, path) in quickAccess.Folders) Add(path, name, isDirectory: true);
        foreach (var (name, path) in quickAccess.RecentFiles) Add(path, name, isDirectory: false);
        if (iconSet == FileIconSet.Windows)
        {
            // ドライブとごみ箱の Shell アイコンはエクスプローラー同等。Material には対応アイコンが無いため絵文字のまま
            foreach (var drive in drives) Add(drive.Path, drive.Name, isDirectory: true);
            Add("shell:RecycleBinFolder", LocalizationService.Text("Text.Sidebar.RecycleBin"), isDirectory: true);
        }

        return icons;
    }

    /// <summary>左ペインに並べるドライブ情報。DriveInfo へのアクセスは UI スレッドをブロックしうるため
    /// 必ずバックグラウンドスレッドで生成し、この不変データだけを UI スレッドへ渡す。</summary>
    private readonly record struct DriveDisplay(string Name, string Path, string Tooltip);

    private static List<DriveDisplay> GetDriveDisplays()
    {
        var result = new List<DriveDisplay>();
        // 列挙も容量取得もドライブ単位で失敗を隔離する（PC ビューの GetEntries と同じ契約）。
        // ここは Task.Run の中なので、1 台の例外を漏らすとサイドバー更新ごと落ちて未観測例外になる。
        foreach (var drive in FileSystemService.GetReadyDrives())
        {
            try
            {
                result.Add(new DriveDisplay(
                    FileSystemService.GetDriveLabel(drive),
                    drive.RootDirectory.FullName,
                    FileSystemService.GetDriveSpace(drive).SizeText));
            }
            catch (Exception ex)
            {
                Logger.LogException($"ドライブをサイドバーに追加できませんでした: {drive.Name}", ex);
            }
        }

        return result;
    }

    private void BuildSidebar(
        QuickAccessService.Snapshot quickAccess,
        IReadOnlyList<DriveDisplay> drives,
        Dictionary<string, Avalonia.Media.IImage> icons,
        bool ownsIcons)
    {
        SidebarItems.Add(new SidebarHeader { Title = LocalizationService.Text("Text.Sidebar.QuickAccess") });
        foreach (var (name, path) in quickAccess.Folders)
        {
            SidebarItems.Add(new SidebarLink
            {
                Name = name,
                Path = path,
                Icon = IconFor(path),
                IconImage = icons.GetValueOrDefault(path),
                OwnsIconImage = ownsIcons,
                IsQuickAccess = true,
                Tooltip = path,
            });
        }

        SidebarItems.Add(new SidebarHeader { Title = "PC" });
        SidebarItems.Add(new SidebarLink { Name = "PC", Path = FileSystemService.ComputerPath, Icon = "🖥", Tooltip = LocalizationService.Text("Text.Sidebar.Drives") });
        foreach (var drive in drives)
        {
            SidebarItems.Add(new SidebarLink
            {
                Name = drive.Name,
                Path = drive.Path,
                Icon = "💾",
                IconImage = icons.GetValueOrDefault(drive.Path),
                OwnsIconImage = ownsIcons,
                Tooltip = drive.Tooltip,
            });
        }

        SidebarItems.Add(new SidebarLink
        {
            Name = LocalizationService.Text("Text.Sidebar.RecycleBin"),
            Path = "shell:RecycleBinFolder",
            Icon = "🗑",
            IconImage = icons.GetValueOrDefault("shell:RecycleBinFolder"),
            OwnsIconImage = ownsIcons,
            IsShellCommand = true,
            Tooltip = LocalizationService.Text("Text.Sidebar.RecycleBinTip"),
        });

        // 最近使用したファイル（クイックアクセスの列挙から。エクスプローラーのホーム相当）
        var recent = quickAccess.RecentFiles;
        if (recent.Count > 0)
        {
            SidebarItems.Add(new SidebarHeader { Title = LocalizationService.Text("Text.Sidebar.RecentFiles") });
            foreach (var (name, path) in recent)
            {
                SidebarItems.Add(new SidebarLink
                {
                    Name = name,
                    Path = path,
                    Icon = "🕒",
                    IconImage = icons.GetValueOrDefault(path),
                    OwnsIconImage = ownsIcons,
                    IsFile = true,
                    Tooltip = path,
                });
            }
        }
    }

    private static string IconFor(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (WindowsPathIdentity.Instance.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.Desktop))) return "🖥";
        if (WindowsPathIdentity.Instance.Equals(path, Path.Combine(profile, "Downloads"))) return "⬇";
        if (WindowsPathIdentity.Instance.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))) return "📄";
        if (WindowsPathIdentity.Instance.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))) return "🖼";
        if (WindowsPathIdentity.Instance.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic))) return "🎵";
        if (WindowsPathIdentity.Instance.Equals(path, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))) return "🎬";
        return "📁";
    }
}
