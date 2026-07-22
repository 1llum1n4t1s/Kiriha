using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>お気に入りバーの内容（settings.json に永続化）。</summary>
    public ObservableCollection<BookmarkNode> BookmarkItems { get; } = new();

    [ObservableProperty]
    private TabViewModel? _selectedTab;

    /// <summary>ウィンドウタイトル（現在のフォルダー名 - Kiriha、タスクバー表示用）。</summary>
    public string WindowTitle
        => SelectedTab is { } tab && tab.Title.Length > 0 ? $"{tab.Title} - Kiriha" : "Kiriha";

    partial void OnSelectedTabChanged(TabViewModel? value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        value?.EnsureCurrentPathAvailable();
        _ = SyncSidebarTreeToCurrentPathAsync();
    }

    /// <summary>ステータスバーの表示状態（表示メニューで切替）。</summary>
    [ObservableProperty]
    private bool _showStatusBar = true;

    [RelayCommand]
    private void ToggleStatusBar() => ShowStatusBar = !ShowStatusBar;

    /// <summary>プレビューペインの幅（Thumb ドラッグで変更）。</summary>
    [ObservableProperty]
    private double _previewWidth = 280;

    /// <summary>お気に入りバーの表示状態（Ctrl+Shift+B で切替、永続化）。</summary>
    [ObservableProperty]
    private bool _showBookmarksBar;

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

    partial void OnShowBookmarksBarChanged(bool value)
    {
        _settings.ShowBookmarksBar = value;
        SettingsService.Save(_settings);
    }

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

    /// <summary>サイドバーにクイックアクセスの代わりに XP 風フォルダーツリーを表示する。</summary>
    [ObservableProperty]
    private bool _showSidebarTree;

    /// <summary>ツリー表示のルート（デスクトップ 1 ノード。子は展開時に遅延列挙）。</summary>
    public ObservableCollection<Models.FolderTreeNode> SidebarTreeRoots { get; } = [];

    /// <summary>ツリービューの選択ノード（TreeView.SelectedItem と双方向。プログラム側からの現在地同期にも使う）。</summary>
    [ObservableProperty]
    private Models.FolderTreeNode? _sidebarTreeSelectedItem;

    partial void OnShowSidebarTreeChanged(bool value)
    {
        _settings.SidebarShowTree = value;
        SettingsService.Save(_settings);
        if (value)
        {
            EnsureSidebarTree();
            _ = SyncSidebarTreeToCurrentPathAsync();
        }
    }

    /// <summary>同期処理の世代。ナビゲーション連打時に古い同期結果で選択を上書きしない。</summary>
    private int _treeSyncGeneration;

    /// <summary>選択中タブの現在フォルダーまでツリーを展開して選択状態にする。</summary>
    public async Task SyncSidebarTreeToCurrentPathAsync()
    {
        try
        {
            await SyncSidebarTreeToCurrentPathCoreAsync();
        }
        catch (Exception ex)
        {
            // 呼び出し元は fire-and-forget のため、ここで握りつぶさず必ずログへ残す
            Logger.LogException("サイドバーツリーの現在地同期に失敗しました", ex);
        }
    }

    private async Task SyncSidebarTreeToCurrentPathCoreAsync()
    {
        if (!ShowSidebarTree || SidebarTreeRoots.Count == 0
            || SelectedTab is not { IsSettingsTab: false } tab)
        {
            return;
        }

        var path = tab.CurrentPath;
        var generation = Interlocked.Increment(ref _treeSyncGeneration);
        var node = SidebarTreeRoots[0];
        node.IsExpanded = true;
        await node.EnsureChildrenAsync();
        if (generation != _treeSyncGeneration)
        {
            return;
        }

        // PC（ドライブ一覧）はマイ コンピュータを選択する
        if (path == FileSystemService.ComputerPath)
        {
            SelectTreeNode(node.Children.FirstOrDefault(c => c.Kind == Models.FolderTreeNode.NodeKind.Computer));
            return;
        }

        while (!WindowsPathIdentity.Instance.Equals(node.Path, path))
        {
            // デスクトップ直下はマイ ドキュメント → デスクトップ配下の実フォルダーの順で優先し、
            // どれにも該当しないパスはマイ コンピュータ（ドライブ）経由で辿る。
            var next = node.Children.FirstOrDefault(c => IsSelfOrAncestorOf(c.Path, path))
                       ?? (node.Kind == Models.FolderTreeNode.NodeKind.Desktop
                           ? node.Children.FirstOrDefault(c => c.Kind == Models.FolderTreeNode.NodeKind.Computer)
                           : null);
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
        if (node is not null)
        {
            node.IsExpanded = true;
            SidebarTreeSelectedItem = node;
        }
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

        var root = new Models.FolderTreeNode
        {
            Name = "デスクトップ",
            Path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Icon = "🖥",
            Kind = Models.FolderTreeNode.NodeKind.Desktop,
        };
        SidebarTreeRoots.Add(root);
        // XP と同じく既定で展開する（子はここで遅延ロードされる）
        root.IsExpanded = true;
    }

    partial void OnPreviewWidthChanged(double value)
    {
        _settings.PreviewWidth = value; // 保存自体は終了時の SaveWindowBounds でまとめて行う
    }

    partial void OnSidebarWidthChanged(double value)
    {
        _settings.SidebarWidth = value; // 保存自体は終了時の SaveWindowBounds でまとめて行う
    }

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
        ? "現在、フォルダーとドライブは Kiriha で開きます。解除すると変更前の動作へ戻します。"
        : "有効にすると、エクスプローラーでフォルダーやドライブを開いたときに Kiriha が起動します。";

    /// <summary>設定タブ: 設定を既定値に戻す（固定タブとお気に入りは保持）。</summary>
    [RelayCommand]
    private void ResetSettings()
    {
        OptShowHidden = false;
        OptShowExtensions = false;
        OptShowCheckBoxes = false;
        OptIconSet = IconSetChoices[0].Label;
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
        ShowBookmarksBar = false;
        ShowSidebar = true;
        ShowSidebarTree = false;
        SidebarWidth = 230;
        VerticalTabWidth = 240;
        SearchBoxWidth = 200;
        ShowPreviewPane = false;
        PreviewWidth = 280;
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
        _settings.ColSizeWidth = 180;
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

    /// <summary>設定タブのアイコンセット選択肢（表示ラベルと設定値の対応の唯一の定義）。</summary>
    private static readonly (string Label, FileIconSet Value)[] IconSetChoices =
    [
        ("現在のオリジナルアイコン", FileIconSet.Original),
        ("マテリアルアイコンテーマ", FileIconSet.Material),
        ("Windows標準のアイコン", FileIconSet.Windows),
    ];

    public IReadOnlyList<string> IconSetOptions { get; } = [.. IconSetChoices.Select(c => c.Label)];

    public string? OptIconSet
    {
        get => (IconSetChoices.FirstOrDefault(c => c.Value == Options.IconSet).Label
                ?? IconSetChoices[0].Label);
        set
        {
            var selected = IconSetChoices.FirstOrDefault(c => c.Label == value);
            if (selected.Label is null || Options.IconSet == selected.Value) return;
            Options.IconSet = selected.Value;
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

    [RelayCommand]
    private void ToggleBookmarksBar() => ShowBookmarksBar = !ShowBookmarksBar;

    public MainWindowViewModel()
    {
        _settings = SettingsService.Load();
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
        Options.Changed += (_, e) =>
        {
            _settings.ShowHidden = Options.ShowHidden;
            _settings.ShowExtensions = Options.ShowExtensions;
            _settings.ShowCheckBoxes = Options.ShowCheckBoxes;
            _settings.IconSet = Options.IconSet.ToString();
            _settings.UseMaterialIcons = false;
            SettingsService.Save(_settings);
            // サイドバー（クイックアクセス等）のアイコンもセット設定に追従させる
            if (e.Kind == ShellOptionKind.IconSet) RefreshSidebar();
        };

        _showBookmarksBar = _settings.ShowBookmarksBar;
        _showSidebar = _settings.ShowSidebar;
        _sidebarWidth = _settings.SidebarWidth is > 120 and < 600 ? Math.Round(_settings.SidebarWidth) : 230;
        _verticalTabWidth = _settings.VerticalTabWidth is >= 180 and <= 420 ? Math.Round(_settings.VerticalTabWidth) : 240;
        // 旧バージョンが保存した小数幅はレイアウト丸めで線がぼやけない整数値へ移行する。
        _settings.SidebarWidth = _sidebarWidth;
        _settings.VerticalTabWidth = _verticalTabWidth;
        _searchBoxWidth = _settings.SearchBoxWidth is > 120 and < 500 ? _settings.SearchBoxWidth : 200;
        _showPreviewPane = _settings.ShowPreviewPane;
        _previewWidth = _settings.PreviewWidth is >= 180 and <= 600 ? _settings.PreviewWidth : 280;
        _showStatusBar = _settings.ShowStatusBar;
        _showSidebarTree = _settings.SidebarShowTree;
        if (_showSidebarTree)
        {
            EnsureSidebarTree();
        }
        ApplyTheme(_settings.ThemePreference);
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
        var tab = new TabViewModel(path, Options, _folderViewSettings, initialViewSettings)
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

        Tabs.Add(tab);
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.PinnedNavigationRequested += OpenPinnedNavigationInNewTab;
        tab.PropertyChanged += Tab_PropertyChanged;
        tab.IsPinned = pinned;
        tab.SetPreviewEnabled(ShowPreviewPane);
        return tab;
    }

    private FolderViewSettings CreateDefaultFolderViewSettings(string path)
        => new()
        {
            Path = path,
            ViewMode = _settings.DefaultViewMode,
            IconSize = _settings.DefaultIconSize,
            SortKey = _settings.DefaultSortKey,
            SortAscending = _settings.DefaultSortAscending,
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

        if (e.PropertyName == nameof(TabViewModel.IsPinned))
        {
            ReorderPinned(tab);
            SavePinned();
        }
        else if (e.PropertyName == nameof(TabViewModel.Title) && ReferenceEquals(tab, SelectedTab))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
        else if (e.PropertyName == nameof(TabViewModel.ViewMode))
        {
            if (tab.IsApplyingFolderViewSettings) return;
            // 最後に使った表示モードを新規タブの既定にする
            _settings.DefaultViewMode = tab.ViewMode.ToString();
        }
        else if (e.PropertyName == nameof(TabViewModel.IconSize))
        {
            if (tab.IsApplyingFolderViewSettings) return;
            // マウスホイールで連続発火するため、Col*Width と同様に即時保存はせず終了時にまとめて保存する
            _settings.DefaultIconSize = tab.IconSize;
        }
        else if (e.PropertyName is nameof(TabViewModel.SortKey) or nameof(TabViewModel.SortAscendingFlag))
        {
            if (tab.IsApplyingFolderViewSettings) return;
            // 最後に使った並べ替えを新規タブの既定にする
            _settings.DefaultSortKey = tab.SortKey;
            _settings.DefaultSortAscending = tab.SortAscendingFlag;
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
            // 選択中タブのフォルダー移動へツリービューの展開・選択を追従させる
            if (ReferenceEquals(tab, SelectedTab))
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
            Tabs.Move(index, target);
        }
    }

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

    // ===== お気に入りバー =====

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
    }

    public void SaveBookmarks()
    {
        SettingsService.Save(_settings);
        RefreshBookmarks();
    }

    /// <summary>お気に入りバーへ追加（parent が null ならルート）。</summary>
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
            // ObservableCollection.Move の通知を ListBox の SelectedItem 双方向バインディングが
            // 正しく維持しないことがあり、並べ替え中に選択が外れることがあるため明示的に復元する。
            var previousSelection = SelectedTab;
            Tabs.Move(from, targetIndex);
            if (!ReferenceEquals(SelectedTab, previousSelection))
            {
                SelectedTab = previousSelection;
            }

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
        }
    }

    private static void RefreshTabs(IEnumerable<TabViewModel> tabs)
    {
        foreach (var tab in tabs.Where(t => !t.IsSettingsTab).ToList()) tab.RefreshCommand.Execute(null);
    }

    private static void SetPinned(IEnumerable<TabViewModel> tabs, bool pinned)
    {
        foreach (var tab in tabs.ToList()) tab.IsPinned = pinned;
    }

    private void ReorderTabs(IEnumerable<TabViewModel> ordered)
    {
        var pinned = ordered.Where(t => t.IsPinned).ToList();
        var unpinned = ordered.Where(t => !t.IsPinned).ToList();
        var settings = Tabs.Where(t => t.IsSettingsTab).ToList();
        var target = pinned.Concat(unpinned).Concat(settings).ToList();
        for (var i = 0; i < target.Count; i++)
        {
            var current = Tabs.IndexOf(target[i]);
            if (current != i) Tabs.Move(current, i);
        }
        SavePinned();
    }

    // ===== サイドバー =====

    /// <summary>クイックアクセスのピン留め変更後などに左ペインを再構築する。</summary>
    public void RefreshSidebar()
        => _ = RefreshSidebarAsync();

    public async Task RefreshSidebarAsync()
    {
        // クイックアクセス列挙とドライブ列挙（DriveInfo.IsReady / 空き容量は切断中の
        // ネットワークドライブでブロックしうる）をまとめてバックグラウンドで取得してから再構築する。
        var iconSet = Options.IconSet;
        var preferLight = iconSet == FileIconSet.Material && MaterialIconService.IsLightTheme();
        var (snapshot, drives, icons) = await Task.Run(() =>
        {
            var snap = QuickAccessService.GetSnapshot();
            var driveList = GetDriveDisplays();
            return (snap, driveList, BuildSidebarIconImages(snap, driveList, iconSet, preferLight));
        });

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
            Add("shell:RecycleBinFolder", "ごみ箱", isDirectory: true);
        }

        return icons;
    }

    /// <summary>左ペインに並べるドライブ情報。DriveInfo へのアクセスは UI スレッドをブロックしうるため
    /// 必ずバックグラウンドスレッドで生成し、この不変データだけを UI スレッドへ渡す。</summary>
    private readonly record struct DriveDisplay(string Name, string Path, string Tooltip);

    private static List<DriveDisplay> GetDriveDisplays()
    {
        var result = new List<DriveDisplay>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            result.Add(new DriveDisplay(
                FileSystemService.GetDriveLabel(drive),
                drive.RootDirectory.FullName,
                $"空き {Models.FileSystemEntry.FormatSize(drive.AvailableFreeSpace)} / {Models.FileSystemEntry.FormatSize(drive.TotalSize)}"));
        }

        return result;
    }

    private void BuildSidebar(
        QuickAccessService.Snapshot quickAccess,
        IReadOnlyList<DriveDisplay> drives,
        Dictionary<string, Avalonia.Media.IImage> icons,
        bool ownsIcons)
    {
        SidebarItems.Add(new SidebarHeader { Title = "クイックアクセス" });
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
        SidebarItems.Add(new SidebarLink { Name = "PC", Path = FileSystemService.ComputerPath, Icon = "🖥", Tooltip = "ドライブ一覧" });
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
            Name = "ごみ箱",
            Path = "shell:RecycleBinFolder",
            Icon = "🗑",
            IconImage = icons.GetValueOrDefault("shell:RecycleBinFolder"),
            OwnsIconImage = ownsIcons,
            IsShellCommand = true,
            Tooltip = "ごみ箱をエクスプローラーで開く",
        });

        // 最近使用したファイル（クイックアクセスの列挙から。エクスプローラーのホーム相当）
        var recent = quickAccess.RecentFiles;
        if (recent.Count > 0)
        {
            SidebarItems.Add(new SidebarHeader { Title = "最近使用したファイル" });
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
