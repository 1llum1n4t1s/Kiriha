using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kiriha.Models;
using Kiriha.Services;
using Kiriha.ViewModels;

namespace Kiriha.Views;

public partial class MainWindow : Window
{
    private PointerPressedEventArgs? _dragPressArgs;
    private ListBox? _dragListBox;
    private Point _dragStartPoint;
    private bool _dragInProgress;

    private TabViewModel? _tabDragTab;
    private Point _tabDragStart;

    /// <summary>最小化される直前の最大化状態（最小化中は Position がセンチネル値になるため、
    /// 閉じたときにこの値で最大化フラグを復元する）。</summary>
    private bool _lastKnownMaximized;

    public MainWindow()
    {
        InitializeComponent();

        // DnD 受け入れ（DragDrop.AllowDrop を立てた要素からバブルしてくる）
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // ListBoxItem が選択処理で PointerPressed を Handled 済みにするため、
        // 通常のバブル購読（XAML の PointerPressed="..."）ではタブの並べ替え開始を検知できない。
        // handledEventsToo: true で Handled 後も確実に拾う。
        TabsListBox.AddHandler(PointerPressedEvent, Tabs_PointerPressed, handledEventsToo: true);

        DataContextChanged += (_, _) =>
        {
            WireTabs();
            if (ViewModel is { } vm)
            {
                ApplyAcrylicTransparency(vm.OptUseAcrylicBackground);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MainWindowViewModel.OptUseAcrylicBackground))
                    {
                        ApplyAcrylicTransparency(vm.OptUseAcrylicBackground);
                    }
                };
            }
        };
        // Opened はトレイ格納からの Show() 復元でも再発火するため、初回起動時の
        // サイズ・位置復元は最初の1回だけに限定する（購読解除して多重適用を防ぐ）。
        EventHandler? onFirstOpened = null;
        onFirstOpened = (_, _) =>
        {
            Opened -= onFirstOpened;
            ApplySavedWindowSize();
        };
        Opened += onFirstOpened;

        // ウィンドウ復帰時: 貼り付け活性の再評価 + PC ビューのドライブ情報更新
        Activated += (_, _) =>
        {
            ViewModel?.NotifyClipboardChanged();
            if (ViewModel?.SelectedTab is { IsSettingsTab: false } tab
                && tab.CurrentPath == FileSystemService.ComputerPath)
            {
                tab.NavigateTo(tab.CurrentPath, record: false);
            }
        };
    }

    /// <summary>ウィンドウのアクリル（半透明ぼかし）効果の ON/OFF。実際の色差し替えは
    /// ThemeService（TabStripBg 等の DynamicResource）が担い、ここでは OS レベルの
    /// 透明合成モードだけを切り替える。</summary>
    private void ApplyAcrylicTransparency(bool enabled)
    {
        TransparencyLevelHint = enabled
            ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None }
            : new[] { WindowTransparencyLevel.None };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // ===== ウィンドウサイズの復元 / 保存 =====

    private void ApplySavedWindowSize()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        // 「ウィンドウのサイズと位置を保存する」が OFF なら復元せず、XAML 既定のサイズ・位置で起動する。
        if (!vm.OptRememberWindowBounds)
        {
            return;
        }

        if (vm.SavedWindowSize is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }

        // 外部モニターを外した等でモニター構成が変わっていた場合、保存位置をそのまま復元すると
        // ウィンドウが画面外に開いて見失うことがあるため、現在接続中のどれかの画面に
        // タイトルバー付近が収まる場合のみ復元する（収まらなければ既定の起動位置に任せる）。
        if (vm.SavedWindowPosition is { } pos && IsPositionOnAnyScreen(pos.X, pos.Y))
        {
            Position = new PixelPoint(pos.X, pos.Y);
        }

        if (vm.SavedWindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private bool IsPositionOnAnyScreen(int x, int y)
    {
        const int probeSize = 40;
        foreach (var screen in Screens.All)
        {
            var wa = screen.WorkingArea;
            if (x + probeSize > wa.X && x < wa.X + wa.Width
                && y + probeSize > wa.Y && y < wa.Y + wa.Height)
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && change.NewValue is WindowState state)
        {
            // 最小化中は Win32 が Position をセンチネル値 (-32000,-32000) で返すため、
            // 最小化に入る直前の最大化状態を覚えておき、閉じるときの保存に使う。
            if (state != WindowState.Minimized)
            {
                _lastKnownMaximized = state == WindowState.Maximized;
            }
            else if (ViewModel is { OptMinimizeToTray: true })
            {
                // 「最小化時にタスクトレイに格納する」ON: タスクバーからも消し、トレイアイコンのみにする（Discord 相当）。
                Hide();
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            ViewModel?.SaveWindowMaximizedFlag(_lastKnownMaximized);
        }
        else
        {
            ViewModel?.SaveWindowBounds(Width, Height, Position.X, Position.Y,
                WindowState == WindowState.Maximized);
        }

        base.OnClosing(e);
    }

    // ===== タスクトレイ =====

    /// <summary>タスクトレイに格納された状態から、最小化前の状態（通常 / 最大化）に復元して最前面に出す。</summary>
    public void RestoreFromTray()
    {
        WindowState = _lastKnownMaximized ? WindowState.Maximized : WindowState.Normal;
        Show();
        Activate();
    }

    // ===== キーボードショートカット / マウスの戻る・進むボタン =====

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            base.OnKeyDown(e);
            return;
        }

        var tab = vm.SelectedTab;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        var handled = true;
        switch (e.Key)
        {
            case Key.B when ctrl && shift:
                vm.ToggleBookmarksBarCommand.Execute(null);
                break;
            case Key.T when ctrl && shift:
                vm.ReopenClosedTabCommand.Execute(null);
                break;
            case Key.T when ctrl:
                vm.NewTabCommand.Execute(null);
                break;
            case Key.N when ctrl:
                // 新しいウィンドウ
                if (Environment.ProcessPath is { } exe)
                {
                    System.Diagnostics.Process.Start(exe);
                }

                break;
            case Key.C when ctrl && shift:
                CopySelectedPaths(tab);
                break;
            case Key.P when ctrl && shift:
                ShowCommandPalette();
                break;
            case Key.F11:
                WindowState = WindowState == WindowState.FullScreen
                    ? WindowState.Normal
                    : WindowState.FullScreen;
                break;
            case Key.D0 when ctrl:
                // Ctrl+0 で表示ズームをリセット（詳細表示）
                if (tab is { IsSettingsTab: false })
                {
                    tab.ViewMode = ViewMode.Details;
                }

                break;
            case Key.W when ctrl:
                if (tab is not null) vm.CloseTabCommand.Execute(tab);
                break;
            case Key.Tab when ctrl:
                vm.SelectAdjacentTab(shift ? -1 : 1);
                break;
            case Key.PageDown when ctrl && shift:
                vm.MoveSelectedTab(1);
                break;
            case Key.PageUp when ctrl && shift:
                vm.MoveSelectedTab(-1);
                break;
            case Key.PageDown when ctrl:
                vm.SelectAdjacentTab(1);
                break;
            case Key.PageUp when ctrl:
                vm.SelectAdjacentTab(-1);
                break;
            case >= Key.D1 and <= Key.D8 when ctrl:
                // Ctrl+1..8 で n 番目のタブ（Chrome 互換）
                var index = e.Key - Key.D1;
                if (index < vm.Tabs.Count)
                {
                    vm.SelectedTab = vm.Tabs[index];
                }

                break;
            case Key.D9 when ctrl:
                // Ctrl+9 は最後のタブ（Chrome 互換）
                if (vm.Tabs.Count > 0)
                {
                    vm.SelectedTab = vm.Tabs[^1];
                }

                break;
            case Key.D when ctrl:
                // Ctrl+D で現在のフォルダーをお気に入りに追加（Chrome 互換）
                if (tab is { IsSettingsTab: false } && tab.CurrentPath != FileSystemService.ComputerPath)
                {
                    vm.AddBookmark(tab.CurrentPath);
                    vm.ShowBookmarksBar = true;
                }

                break;
            case Key.N when ctrl && shift:
                tab?.CreateNewFolder();
                break;
            case Key.P when alt:
                vm.TogglePreviewPaneCommand.Execute(null);
                break;
            case Key.Home when alt:
                tab?.NavigateTo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                break;
            case Key.F4:
                FocusPathBox(tab);
                break;
            case Key.F3:
                FocusSearchBox();
                break;
            case Key.F1:
                // F1 でヘルプ（設定タブのショートカット一覧）
                vm.OpenSettingsCommand.Execute(null);
                break;
            case Key.L when ctrl:
            case Key.D when alt:
                FocusPathBox(tab);
                break;
            case Key.F when ctrl:
            case Key.E when ctrl:
                FocusSearchBox();
                break;
            case Key.F5:
                tab?.RefreshCommand.Execute(null);
                break;
            case Key.Left when alt:
                tab?.GoBackCommand.Execute(null);
                break;
            case Key.Right when alt:
                tab?.GoForwardCommand.Execute(null);
                break;
            case Key.Up when alt:
                tab?.GoUpCommand.Execute(null);
                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // マウスの「戻る / 進む」ボタン
        if (ViewModel?.SelectedTab is { } tab)
        {
            if (e.InitialPressMouseButton == MouseButton.XButton1)
            {
                tab.GoBackCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.InitialPressMouseButton == MouseButton.XButton2)
            {
                tab.GoForwardCommand.Execute(null);
                e.Handled = true;
            }
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // タブバー / お気に入りバー上の縦ホイールは横スクロールに変換
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Source is Visual wheelSource)
        {
            var overTabs = wheelSource.FindAncestorOfType<ListBox>()?.Classes.Contains("tabs") == true;
            var overBookmarks = wheelSource.GetVisualAncestors().OfType<Border>()
                .Any(b => b.Classes.Contains("bookmarkbar"));
            if (overTabs || overBookmarks)
            {
                var scroller = overTabs
                    ? wheelSource.FindAncestorOfType<ListBox>()?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
                    : wheelSource.GetVisualAncestors().OfType<Border>()
                        .First(b => b.Classes.Contains("bookmarkbar"))
                        .GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroller is not null)
                {
                    scroller.Offset = scroller.Offset.WithX(scroller.Offset.X - e.Delta.Y * 48);
                    e.Handled = true;
                    return;
                }
            }
        }

        // Ctrl+ホイールで表示ズーム（アイコン表示中は Finder 同様の無段階、他は表示モードを段階切替）
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && ViewModel?.SelectedTab is { IsSettingsTab: false } tab
            && (e.Source as Visual)?.FindAncestorOfType<ListBox>() is { } list
            && (list.Classes.Contains("files") || list.Classes.Contains("icons")))
        {
            if (tab.IsIconsView)
            {
                tab.IconSize = Math.Clamp(tab.IconSize + e.Delta.Y * 8, 24, 160);
            }
            else
            {
                var order = new[]
                {
                    ViewMode.Details, ViewMode.List, ViewMode.SmallIcons,
                    ViewMode.MediumIcons, ViewMode.LargeIcons, ViewMode.ExtraLargeIcons,
                };
                var index = Array.IndexOf(order, tab.ViewMode);
                var next = Math.Clamp(index + (e.Delta.Y > 0 ? 1 : -1), 0, order.Length - 1);
                tab.ViewMode = order[next];
            }

            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    /// <summary>選択項目のフルパスをテキストとしてコピー（エクスプローラーの「パスのコピー」）。</summary>
    private async void CopySelectedPaths(TabViewModel? tab)
    {
        if (tab is null || tab.Selection.Count == 0 || Clipboard is null)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, tab.Selection.Select(s => $"\"{s.FullPath}\""));
        await Clipboard.SetTextAsync(text);
    }

    private void FocusPathBox(TabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        tab.IsEditingPath = true;
        Dispatcher.UIThread.Post(() =>
        {
            var box = this.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.IsEffectivelyVisible && t.PlaceholderText != "検索");
            box?.Focus();
            box?.SelectAll();
        });
    }

    private void FocusSearchBox()
    {
        var box = this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(t => t.IsEffectivelyVisible && t.Classes.Contains("searchbox"));
        box?.Focus();
    }

    private void Home_Click(object? sender, RoutedEventArgs e)
        => ViewModel?.SelectedTab?.NavigateTo(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private void CommandPalette_Click(object? sender, RoutedEventArgs e) => ShowCommandPalette();

    /// <summary>アプリ内の機能を名前・カテゴリで横断検索して実行する。</summary>
    private void ShowCommandPalette()
    {
        if (ViewModel?.SelectedTab is not { IsSettingsTab: false }) return;
        var dialog = new CommandPaletteWindow(ViewModel.OptUseAcrylicBackground);
        dialog.CommandSelected += (_, command) => ExecuteFeature(command);
        _ = dialog.ShowDialog(this);
    }

    private void ExecuteFeature(FeatureCommand command)
    {
        if (ViewModel?.SelectedTab is not { IsSettingsTab: false } tab) return;
        switch (command.Kind)
        {
            case "location":
                var path = ResolveFeatureLocation(command.Value);
                if (path.Length > 0 && Directory.Exists(path)) tab.NavigateTo(path);
                else if (command.Value == "Computer") tab.NavigateTo(FileSystemService.ComputerPath);
                else tab.StatusText = $"場所が見つかりません: {command.Title}";
                break;
            case "filter":
                tab.ApplyExtensionFilter(command.Value, command.Title.Replace("で絞り込み", ""));
                break;
            case "action":
                ExecutePaletteAction(tab, command.Value);
                break;
        }
    }

    private static string ResolveFeatureLocation(string key)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return key switch
        {
            "Downloads" => Path.Combine(profile, "Downloads"), "Windows" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Temp" => Path.GetTempPath(), "Public" => Environment.GetEnvironmentVariable("PUBLIC") ?? "",
            "OneDrive" => Environment.GetEnvironmentVariable("OneDrive") ?? "", "Users" => Directory.GetParent(profile)?.FullName ?? "",
            "Computer" => "", _ when Enum.TryParse<Environment.SpecialFolder>(key, out var folder) => Environment.GetFolderPath(folder),
            _ => "",
        };
    }

    private void ExecutePaletteAction(TabViewModel tab, string action)
    {
        var list = FindActiveFileList();
        switch (action)
        {
            case "clear-filter": tab.SearchText = ""; tab.ClearExtensionFilter(); break;
            case "select-all": list?.SelectAll(); break;
            case "select-none": list?.UnselectAll(); break;
            case "select-invert": SelectInvert_Click(null, new RoutedEventArgs()); break;
            case "select-folders": SelectMatching(list, e => e.IsDirectory); break;
            case "select-files": SelectMatching(list, e => !e.IsDirectory); break;
            case "sort-name-asc": tab.SortKey = "Name"; tab.SortAscendingFlag = true; tab.RefreshCommand.Execute(null); break;
            case "sort-name-desc": tab.SortKey = "Name"; tab.SortAscendingFlag = false; tab.RefreshCommand.Execute(null); break;
            case "sort-modified-desc": tab.SortKey = "Modified"; tab.SortAscendingFlag = false; tab.RefreshCommand.Execute(null); break;
            case "sort-size-desc": tab.SortKey = "Size"; tab.SortAscendingFlag = false; tab.RefreshCommand.Execute(null); break;
            default:
                if (action.StartsWith("view-") && Enum.TryParse<ViewMode>(action[5..], out var mode)) tab.ViewMode = mode;
                break;
        }
    }

    private static void SelectMatching(ListBox? list, Func<FileSystemEntry, bool> predicate)
    {
        if (list?.SelectedItems is not { } selected) return;
        selected.Clear();
        foreach (var entry in list.Items.OfType<FileSystemEntry>().Where(predicate)) selected.Add(entry);
    }

    private void SelectFolders_Click(object? sender, RoutedEventArgs e)
        => SelectMatching(FindActiveFileList(), entry => entry.IsDirectory);

    private void SelectFiles_Click(object? sender, RoutedEventArgs e)
        => SelectMatching(FindActiveFileList(), entry => !entry.IsDirectory);

    /// <summary>アプリ機能として登録された25件の場所移動を通常のコマンドバーメニューに展開する。</summary>
    private void LocationsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var command in FeatureCatalog.All.Where(item => item.Kind == "location"))
        {
            var item = new MenuItem { Header = command.Title, Icon = new TextBlock { Text = "📁" } };
            item.Click += (_, _) => ExecuteFeature(command);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }

    /// <summary>ファイル種別フィルターを用途別のサブメニューに分けて通常UIから利用できるようにする。</summary>
    private void FileTypeFilterButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var clear = new MenuItem { Header = "絞り込みを解除" };
        clear.Click += (_, _) => ExecuteFeature(FeatureCatalog.All.First(item => item.Value == "clear-filter"));
        flyout.Items.Add(clear);
        flyout.Items.Add(new Separator());

        var filters = FeatureCatalog.All.Where(item => item.Kind == "filter").ToList();
        (string Name, int Start, int Count)[] groups =
        [
            ("メディア・文書", 0, 13), ("アプリ・システム", 13, 9),
            ("プログラミング", 22, 16), ("Web・データ", 38, 10),
            ("設計・コンテンツ", 48, 7), ("その他", 55, 5),
        ];
        foreach (var (name, start, count) in groups)
        {
            var group = new MenuItem { Header = name };
            foreach (var command in filters.Skip(start).Take(count))
            {
                var item = new MenuItem { Header = command.Title.Replace("で絞り込み", "") };
                item.Click += (_, _) => ExecuteFeature(command);
                group.Items.Add(item);
            }
            flyout.Items.Add(group);
        }
        flyout.ShowAt(button);
    }

    /// <summary>ホームボタンの右クリック（よく使うフォルダーへジャンプ）。</summary>
    private void Home_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Button { DataContext: TabViewModel tab } button)
        {
            return;
        }

        e.Handled = true;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string Name, string Path)[] wellKnown =
        [
            ("デスクトップ", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("ダウンロード", Path.Combine(profile, "Downloads")),
            ("ドキュメント", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("ピクチャ", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("ミュージック", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("ビデオ", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            ("AppData (Roaming)", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("AppData (Local)", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        ];

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (name, path) in wellKnown.Where(w => Directory.Exists(w.Path)))
        {
            var captured = path;
            var item = new MenuItem { Header = name, Icon = new TextBlock { Text = "📁" } };
            item.Click += (_, _) => tab.NavigateTo(captured);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void PreviewThumb_DragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            // プレビューは右側なので左ドラッグで拡大
            vm.PreviewWidth = Math.Clamp(vm.PreviewWidth - e.Vector.X, 180, 600);
        }
    }

    private void NewWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (Environment.ProcessPath is { } exe)
        {
            System.Diagnostics.Process.Start(exe);
        }
    }

    /// <summary>詳細表示ヘッダーの右クリック（列の表示 / 非表示、エクスプローラー互換）。</summary>
    private static void ColumnHeader_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not StackPanel { DataContext: TabViewModel tab } panel)
        {
            return;
        }

        e.Handled = true;
        var flyout = new MenuFlyout();
        (string Header, string Key, bool Checked)[] columns =
        [
            ("更新日時", "Modified", tab.ShowColModified),
            ("作成日時", "Created", tab.ShowColCreated),
            ("種類", "Type", tab.ShowColType),
            ("サイズ", "Size", tab.ShowColSize),
        ];
        foreach (var (header, key, isChecked) in columns)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked };
            var captured = key;
            item.Click += (_, _) => tab.ToggleColumnCommand.Execute(captured);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(panel, showAtPointer: true);
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            box.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && box.DataContext is TabViewModel tab)
        {
            // Enter でサブフォルダーを含む検索（エクスプローラーの検索と同等）
            _ = tab.SearchRecursiveAsync();
            e.Handled = true;
        }
    }

    private void SearchClear_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is TabViewModel tab)
        {
            tab.SearchText = "";
        }
    }

    // ===== タブの RenameRequested 購読 =====

    private void WireTabs()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        foreach (var tab in vm.Tabs)
        {
            tab.RenameRequested -= Tab_RenameRequested;
            tab.RenameRequested += Tab_RenameRequested;
        }

        vm.Tabs.CollectionChanged += Tabs_CollectionChanged;
    }

    private void Tabs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<TabViewModel>())
            {
                item.RenameRequested -= Tab_RenameRequested;
                item.RenameRequested += Tab_RenameRequested;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<TabViewModel>())
            {
                item.RenameRequested -= Tab_RenameRequested;
            }
        }
    }

    private async void Tab_RenameRequested(object? sender, FileSystemEntry entry)
    {
        if (sender is not TabViewModel tab)
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(entry.Name);
        var selectionLength = entry.IsDirectory ? entry.Name.Length : stem.Length;
        var newName = await PromptTextAsync("名前の変更", entry.Name, selectionLength);
        if (newName is not null)
        {
            tab.CommitRename(entry, newName);
        }
    }

    /// <summary>テキスト入力ダイアログ（名前の変更 / お気に入りフォルダー名など）。確定時のみ文字列を返す。</summary>
    private async Task<string?> PromptTextAsync(string title, string initial, int? selectionLength = null)
    {
        var box = new TextBox { Text = initial, MinWidth = 300 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };

        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok, cancel },
                    },
                },
            },
        };

        var confirmed = false;
        ok.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectionStart = 0;
            box.SelectionEnd = selectionLength ?? initial.Length;
        };

        await dialog.ShowDialog(this);
        return confirmed ? box.Text : null;
    }

    // ===== タイトルバー / タブストリップ =====

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // タブやボタンの上はそれぞれの操作に任せる（ここで掴むとタブ操作を阻害する）
        if (e.Source is Visual visual
            && (visual.FindAncestorOfType<ListBoxItem>() is not null
                || visual.FindAncestorOfType<Button>() is not null))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // タブバーの空き領域ダブルクリックは新規タブ（Chrome 互換）、それ以外は最大化切替
        if ((e.Source as Visual)?.FindAncestorOfType<ListBox>()?.Classes.Contains("tabs") == true
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is null)
        {
            ViewModel?.NewTabCommand.Execute(null);
            return;
        }

        ToggleMaximize();
    }

    /// <summary>タブ右クリックメニューが開いたとき「最近閉じたタブ」サブメニューを構築する。</summary>
    private void TabMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || ViewModel is not { } vm)
        {
            return;
        }

        var recent = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "recent");
        var tabActions = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "tabActions");
        if (tabActions is not null && menu.DataContext is TabViewModel contextTab)
        {
            tabActions.Items.Clear();
            foreach (var action in ContextActionCatalog.For(ActionScope.Tab))
            {
                var item = new MenuItem { Header = action.Title };
                item.Click += async (_, _) => await ExecuteTabContextActionAsync(action, contextTab);
                tabActions.Items.Add(item);
            }
        }
        if (recent is null)
        {
            return;
        }

        recent.Items.Clear();
        var paths = vm.ClosedTabPaths;
        if (paths.Count == 0)
        {
            recent.Items.Add(new MenuItem { Header = "(なし)", IsEnabled = false });
            return;
        }

        foreach (var path in paths)
        {
            var name = path == FileSystemService.ComputerPath
                ? "PC"
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path;
            var captured = path;
            var item = new MenuItem { Header = name, Icon = new TextBlock { Text = "📁" } };
            item.Click += (_, _) => vm.ReopenClosedPath(captured);
            recent.Items.Add(item);
        }
    }

    private async Task ExecuteTabContextActionAsync(ContextAction action, TabViewModel tab)
    {
        if (ViewModel is not { } vm) return;
        string? text = action.Id switch
        {
            "tab.copy-title" => tab.Title,
            "tab.copy-uri" => tab.CurrentPath == FileSystemService.ComputerPath ? "PC" : new Uri(tab.CurrentPath).AbsoluteUri,
            "tab.copy-markdown" => $"[{tab.Title}]({(tab.CurrentPath == FileSystemService.ComputerPath ? "PC" : new Uri(tab.CurrentPath).AbsoluteUri)})",
            "tab.copy-all-paths" => string.Join(Environment.NewLine, vm.Tabs.Where(t => !t.IsSettingsTab).Select(t => t.CurrentPath)),
            _ => null,
        };
        if (text is not null && Clipboard is not null)
        {
            await Clipboard.SetTextAsync(text);
            tab.StatusText = "クリップボードにコピーしました";
            return;
        }
        vm.ExecuteTabManagement(action.Id, tab);
    }

    private async void TabCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is TabViewModel tab && Clipboard is not null)
        {
            await Clipboard.SetTextAsync(tab.CurrentPath == FileSystemService.ComputerPath ? "PC" : tab.CurrentPath);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    // ===== タブのドラッグ並べ替え / 中クリック閉じ / コンテキストメニュー =====

    private static TabViewModel? TabUnderPointer(RoutedEventArgs e)
        => (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as TabViewModel;


    // PointerMoved 中はドラッグ元の ListBoxItem がポインターをキャプチャしたままになり、
    // e.Source がドラッグ開始時の要素に固定され続けて動かない（現在地の要素に更新されない）。
    // そのため座標ベースで都度ヒットテストし直す必要がある。
    private TabViewModel? TabUnderPoint(Point point)
        => (this.InputHitTest(point) as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext as TabViewModel;

    private void Tabs_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _tabDragTab = TabUnderPointer(e);
            _tabDragStart = e.GetPosition(this);
        }
    }

    private void Tabs_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragTab is null || ViewModel is not { } vm
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _tabDragStart.X) < 8)
        {
            return;
        }

        if (TabUnderPoint(position) is { } target && target != _tabDragTab)
        {
            vm.MoveTab(_tabDragTab, vm.Tabs.IndexOf(target));
        }
    }

    private void Tabs_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            if (TabUnderPointer(e) is { } tab)
            {
                // Chrome と同じく中クリックでタブを閉じる（固定タブは閉じない）
                if (!tab.IsPinned)
                {
                    ViewModel?.CloseTabCommand.Execute(tab);
                }
            }
            else
            {
                // タブバーの空き領域の中クリックで新規タブ（Chrome 互換）
                ViewModel?.NewTabCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Right && TabUnderPointer(e) is null)
        {
            // タブバー背景の右クリックメニュー
            var flyout = new MenuFlyout();
            var newTab = new MenuItem { Header = "新しいタブ", InputGesture = new KeyGesture(Key.T, KeyModifiers.Control) };
            newTab.Click += (_, _) => ViewModel?.NewTabCommand.Execute(null);
            flyout.Items.Add(newTab);
            var reopen = new MenuItem { Header = "閉じたタブを開く", InputGesture = new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift) };
            reopen.Click += (_, _) => ViewModel?.ReopenClosedTabCommand.Execute(null);
            flyout.Items.Add(reopen);
            flyout.ShowAt((Control)sender!, showAtPointer: true);
            e.Handled = true;
        }

        _tabDragTab = null;
    }

    private void TabNewRight_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is TabViewModel tab)
        {
            ViewModel?.NewTabToRight(tab);
        }
    }

    private void TabDuplicate_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is TabViewModel tab)
        {
            ViewModel?.DuplicateTab(tab);
        }
    }

    private void TabCloseRight_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is TabViewModel tab)
        {
            ViewModel?.CloseTabsToRight(tab);
        }
    }

    private void TabCloseOthers_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is TabViewModel tab)
        {
            ViewModel?.CloseOtherTabs(tab);
        }
    }

    // ===== 履歴メニュー（戻る / 進むボタンの右クリック、Chrome 互換） =====

    private void NavBack_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => ShowHistoryMenu(sender, e, back: true);

    private void NavForward_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => ShowHistoryMenu(sender, e, back: false);

    private static void ShowHistoryMenu(object? sender, PointerReleasedEventArgs e, bool back)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Button { DataContext: TabViewModel tab } button)
        {
            return;
        }

        var history = back ? tab.BackHistory : tab.ForwardHistory;
        if (history.Count == 0)
        {
            return;
        }

        e.Handled = true;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        for (var i = 0; i < history.Count && i < 15; i++)
        {
            var path = history[i];
            var name = path == FileSystemService.ComputerPath
                ? "PC"
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path;
            var steps = i + 1;
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => tab.GoHistorySteps(steps, back);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    // ===== パンくずのシェブロン（サブフォルダーのドロップダウン） =====

    private void CrumbChevron_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BreadcrumbSegment segment } button
            || button.FindAncestorOfType<ItemsControl>()?.DataContext is not TabViewModel tab)
        {
            return;
        }

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        try
        {
            IEnumerable<(string Name, string Path)> children;
            if (segment.Path == FileSystemService.ComputerPath)
            {
                children = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => (FileSystemService.GetDriveLabel(d), d.RootDirectory.FullName));
            }
            else
            {
                children = Directory.EnumerateDirectories(segment.Path)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .Select(p => (Path.GetFileName(p), p));
            }

            var any = false;
            foreach (var (name, path) in children)
            {
                any = true;
                var captured = path;
                var item = new MenuItem { Header = name, Icon = new TextBlock { Text = "📁" } };
                item.Click += (_, _) => tab.NavigateTo(captured);
                flyout.Items.Add(item);
            }

            if (!any)
            {
                flyout.Items.Add(new MenuItem { Header = "(サブフォルダーなし)", IsEnabled = false });
            }
        }
        catch
        {
            flyout.Items.Add(new MenuItem { Header = "(アクセスできません)", IsEnabled = false });
        }

        flyout.ShowAt(button);
    }

    /// <summary>パンくずセグメントの中クリックでバックグラウンドの新しいタブに開く。</summary>
    private void Crumb_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle
            && sender is Button { DataContext: BreadcrumbSegment segment })
        {
            ViewModel?.OpenInNewTabBackground(segment.Path);
            e.Handled = true;
        }
    }

    /// <summary>アドレスバーの右クリック（アドレスのコピー / 編集）。</summary>
    private void BreadcrumbBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Border { DataContext: TabViewModel tab } border)
        {
            return;
        }

        e.Handled = true;
        var flyout = new MenuFlyout();

        var copy = new MenuItem { Header = "アドレスのコピー" };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is not null)
            {
                await Clipboard.SetTextAsync(tab.CurrentPath == FileSystemService.ComputerPath ? "PC" : tab.CurrentPath);
            }
        };
        flyout.Items.Add(copy);

        var edit = new MenuItem { Header = "アドレスの編集", InputGesture = new KeyGesture(Key.L, KeyModifiers.Control) };
        edit.Click += (_, _) => FocusPathBox(tab);
        flyout.Items.Add(edit);

        flyout.ShowAt(border, showAtPointer: true);
    }

    private void TabReopen_Click(object? sender, RoutedEventArgs e)
        => ViewModel?.ReopenClosedTabCommand.Execute(null);

    /// <summary>「上へ」ボタンの右クリックで祖先フォルダー一覧を表示する。</summary>
    private static void NavUp_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Button { DataContext: TabViewModel tab } button
            || tab.Breadcrumbs.Count <= 1)
        {
            return;
        }

        e.Handled = true;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        // 現在地を除く祖先（近い順）
        foreach (var segment in tab.Breadcrumbs.Reverse().Skip(1))
        {
            var captured = segment.Path;
            var item = new MenuItem { Header = segment.Name, Icon = new TextBlock { Text = "📁" } };
            item.Click += (_, _) => tab.NavigateTo(captured);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void SidebarThumb_DragDelta(object? sender, VectorEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.SidebarWidth = Math.Clamp(vm.SidebarWidth + e.Vector.X, 140, 500);
        }
    }

    private void SelectInvert_Click(object? sender, RoutedEventArgs e)
    {
        if (FindActiveFileList() is not { DataContext: TabViewModel tab } listBox
            || listBox.SelectedItems is not { } selected)
        {
            return;
        }

        var current = selected.OfType<FileSystemEntry>().ToHashSet();
        listBox.UnselectAll();
        foreach (var entry in tab.Entries.Where(entry => !current.Contains(entry)))
        {
            selected.Add(entry);
        }
    }

    private void PinToQuickAccess_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedTab is not { } tab || tab.CurrentPath == FileSystemService.ComputerPath)
        {
            return;
        }

        var handle = TryGetPlatformHandle();
        if (handle is not null
            && ShellContextMenuService.InvokeVerb(handle.Handle, tab.CurrentPath, "pintohome"))
        {
            ViewModel?.RefreshSidebar();
        }
    }

    private void AddBookmark_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { SelectedTab: { IsSettingsTab: false } tab } vm
            && tab.CurrentPath != FileSystemService.ComputerPath)
        {
            vm.AddBookmark(tab.CurrentPath);
            vm.ShowBookmarksBar = true;
        }
    }

    private void CopyPath_Click(object? sender, RoutedEventArgs e)
        => CopySelectedPaths(ViewModel?.SelectedTab);

    // ===== アドレスバー（パンくず / 直接入力） =====

    private void BreadcrumbBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        if (sender is Border { DataContext: TabViewModel tab } border)
        {
            tab.IsEditingPath = true;
            e.Handled = true;
            Dispatcher.UIThread.Post(() =>
            {
                var box = border.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                if (box is not null)
                {
                    box.Focus();
                    box.SelectAll();
                }
            });
        }
    }

    private void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TabViewModel tab })
        {
            return;
        }

        if (e.Key is Key.Enter)
        {
            tab.NavigateToPathText();
            tab.IsEditingPath = false;
            e.Handled = true;
        }
        else if (e.Key is Key.Escape)
        {
            tab.IsEditingPath = false;
            e.Handled = true;
        }
    }

    private void PathBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: TabViewModel tab })
        {
            tab.IsEditingPath = false;
        }
    }

    // ===== 左ペイン =====

    private void Sidebar_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox || listBox.SelectedItem is null)
        {
            return;
        }

        var selected = listBox.SelectedItem;
        listBox.SelectedItem = null;

        if (selected is not SidebarLink link || listBox.DataContext is not TabViewModel tab)
        {
            return;
        }

        // 最近使用したファイルは関連付けアプリで起動
        if (link.IsFile)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(link.Path) { UseShellExecute = true });
            }
            catch
            {
                // 起動失敗は無視
            }

            return;
        }

        // ごみ箱などの shell: 項目はエクスプローラーに委譲して開く
        if (link.IsShellCommand)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", link.Path) { UseShellExecute = true });
            }
            catch
            {
                // 開けない場合は無視
            }

            return;
        }

        tab.NavigateTo(link.Path);
    }

    /// <summary>左ペインの右クリック（新しいタブで開く / ピン留め解除 / プロパティ）と中クリック。</summary>
    private void Sidebar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not ListBox listBox
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext is not SidebarLink link)
        {
            return;
        }

        // 中クリックでバックグラウンドの新しいタブに開く（Chrome 互換）
        if (e.InitialPressMouseButton == MouseButton.Middle && !link.IsShellCommand)
        {
            ViewModel?.OpenInNewTabBackground(link.Path);
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        var flyout = new MenuFlyout();

        if (link.IsShellCommand)
        {
            // ごみ箱: 開く / 空にする
            var openBin = new MenuItem { Header = "開く" };
            openBin.Click += (_, _) => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", link.Path) { UseShellExecute = true });
            flyout.Items.Add(openBin);

            var empty = new MenuItem { Header = "ごみ箱を空にする" };
            empty.Click += (_, _) =>
            {
                if (TryGetPlatformHandle() is { } h)
                {
                    FileOperationService.EmptyRecycleBin(h.Handle);
                }
            };
            flyout.Items.Add(empty);

            flyout.ShowAt(listBox, showAtPointer: true);
            e.Handled = true;
            return;
        }

        var openNew = new MenuItem { Header = "新しいタブで開く" };
        openNew.Click += (_, _) => ViewModel?.OpenInNewTab(link.Path);
        flyout.Items.Add(openNew);

        if (link.IsQuickAccess)
        {
            var unpin = new MenuItem { Header = "クイックアクセスからピン留めを外す" };
            unpin.Click += (_, _) =>
            {
                var handle = TryGetPlatformHandle();
                if (handle is not null && ShellContextMenuService.InvokeVerb(handle.Handle, link.Path, "unpinfromhome"))
                {
                    ViewModel?.RefreshSidebar();
                }
            };
            flyout.Items.Add(unpin);
        }

        if (link.Path != FileSystemService.ComputerPath)
        {
            flyout.Items.Add(new Separator());
            var props = new MenuItem { Header = "プロパティ" };
            props.Click += (_, _) => FileOperationService.ShowProperties(link.Path);
            flyout.Items.Add(props);
        }

        flyout.ShowAt(listBox, showAtPointer: true);
        e.Handled = true;
    }

    // ===== お気に入りバー =====

    private void Bookmark_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BookmarkNode node } button || ViewModel is not { } vm)
        {
            return;
        }

        if (node.IsFolder)
        {
            var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            PopulateBookmarkMenu(flyout.Items, node.Children!, vm);
            flyout.ShowAt(button);
        }
        else if (node.Path is { } path)
        {
            vm.SelectedTab?.NavigateTo(path);
        }
    }

    private void PopulateBookmarkMenu(ItemCollection items, List<BookmarkNode> nodes, MainWindowViewModel vm)
    {
        if (nodes.Count == 0)
        {
            items.Add(new MenuItem { Header = "(空)", IsEnabled = false });
            return;
        }

        foreach (var node in nodes)
        {
            if (node.IsFolder)
            {
                var folder = new MenuItem { Header = node.Name, Icon = new TextBlock { Text = "📁" } };
                PopulateBookmarkMenu(folder.Items, node.Children!, vm);
                items.Add(folder);
            }
            else
            {
                var item = new MenuItem { Header = node.Name, Icon = new TextBlock { Text = "⭐" } };
                var captured = node;
                item.Click += (_, _) =>
                {
                    if (captured.Path is { } path)
                    {
                        vm.SelectedTab?.NavigateTo(path);
                    }
                };
                items.Add(item);
            }
        }
    }

    /// <summary>お気に入り項目の右クリック（Chrome 互換: 開き方 / 名前変更 / 削除）。</summary>
    private async void BookmarkItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button { DataContext: BookmarkNode node } button
            || ViewModel is not { } vm)
        {
            return;
        }

        // 中クリックでバックグラウンドの新しいタブに開く（Chrome 互換）
        if (e.InitialPressMouseButton == MouseButton.Middle && !node.IsFolder && node.Path is { } midPath)
        {
            vm.OpenInNewTabBackground(midPath);
            e.Handled = true;
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        e.Handled = true;
        var flyout = new MenuFlyout();

        if (!node.IsFolder && node.Path is { } path)
        {
            var openNew = new MenuItem { Header = "新しいタブで開く" };
            openNew.Click += (_, _) => vm.OpenInNewTab(path);
            flyout.Items.Add(openNew);
            flyout.Items.Add(new Separator());
        }

        if (node.IsFolder)
        {
            var addChild = new MenuItem { Header = "フォルダを追加..." };
            addChild.Click += async (_, _) =>
            {
                var name = await PromptTextAsync("フォルダを追加", "新しいフォルダ");
                if (name is not null)
                {
                    vm.AddBookmarkFolder(name, node);
                }
            };
            flyout.Items.Add(addChild);
            flyout.Items.Add(new Separator());
        }

        var rename = new MenuItem { Header = "名前を変更..." };
        rename.Click += async (_, _) =>
        {
            var newName = await PromptTextAsync("お気に入りの名前を変更", node.Name);
            if (newName is not null)
            {
                vm.RenameBookmark(node, newName);
            }
        };
        flyout.Items.Add(rename);

        var remove = new MenuItem { Header = "削除" };
        remove.Click += (_, _) => vm.RemoveBookmark(node);
        flyout.Items.Add(remove);

        flyout.ShowAt(button, showAtPointer: true);
        await Task.CompletedTask;
    }

    /// <summary>お気に入りバー背景の右クリック（Chrome 互換: フォルダ追加 / ソート / 表示切替）。</summary>
    private void BookmarkBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right
            || sender is not Border bar
            || ViewModel is not { } vm)
        {
            return;
        }

        // 項目ボタン上は BookmarkItem_PointerReleased が処理済み
        if ((e.Source as Visual)?.FindAncestorOfType<Button>() is not null)
        {
            return;
        }

        e.Handled = true;
        var flyout = new MenuFlyout();

        var addCurrent = new MenuItem { Header = "現在のフォルダーを追加" };
        addCurrent.Click += (_, _) =>
        {
            if (vm.SelectedTab is { } tab && tab.CurrentPath != FileSystemService.ComputerPath)
            {
                vm.AddBookmark(tab.CurrentPath);
            }
        };
        flyout.Items.Add(addCurrent);

        var addFolder = new MenuItem { Header = "フォルダを追加..." };
        addFolder.Click += async (_, _) =>
        {
            var name = await PromptTextAsync("フォルダを追加", "新しいフォルダ");
            if (name is not null)
            {
                vm.AddBookmarkFolder(name);
            }
        };
        flyout.Items.Add(addFolder);

        flyout.Items.Add(new Separator());

        var sortName = new MenuItem { Header = "名前順で並べ替え" };
        sortName.Click += (_, _) => vm.SortBookmarks(byPath: false);
        flyout.Items.Add(sortName);

        var sortPath = new MenuItem { Header = "パス名順で並べ替え" };
        sortPath.Click += (_, _) => vm.SortBookmarks(byPath: true);
        flyout.Items.Add(sortPath);

        flyout.Items.Add(new Separator());

        var hide = new MenuItem { Header = "お気に入りバーを表示しない" };
        hide.Click += (_, _) => vm.ShowBookmarksBar = false;
        flyout.Items.Add(hide);

        flyout.ShowAt(bar, showAtPointer: true);
    }

    // ===== 詳細表示のカラム幅変更 =====

    private void ColumnThumb_DragDelta(object? sender, VectorEventArgs e)
    {
        if (sender is not Thumb { DataContext: TabViewModel tab } thumb)
        {
            return;
        }

        switch (thumb.Tag as string)
        {
            case "Name":
                tab.ColNameWidth = Math.Max(100, tab.ColNameWidth + e.Vector.X);
                break;
            case "Modified":
                tab.ColModifiedWidth = Math.Max(60, tab.ColModifiedWidth + e.Vector.X);
                break;
            case "Type":
                tab.ColTypeWidth = Math.Max(60, tab.ColTypeWidth + e.Vector.X);
                break;
            case "Size":
                tab.ColSizeWidth = Math.Max(60, tab.ColSizeWidth + e.Vector.X);
                break;
        }
    }

    // ===== ファイル一覧 =====

    private void FileList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { DataContext: TabViewModel tab, SelectedItem: FileSystemEntry entry })
        {
            tab.Open(entry);
        }
    }

    private void FileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { DataContext: TabViewModel tab } listBox && listBox.IsVisible)
        {
            tab.SetSelection(listBox.SelectedItems?.OfType<FileSystemEntry>().ToList() ?? []);
        }
    }

    private void FileList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not ListBox { DataContext: TabViewModel tab } listBox)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.X when ctrl:
                tab.CutCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.C when ctrl:
                tab.CopyCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.V when ctrl:
                tab.PasteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.A when ctrl:
                listBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Delete when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                // Shift+Delete で完全削除（エクスプローラー互換）
                tab.DeletePermanentCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                tab.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2:
                tab.RenameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                // Alt+Enter でプロパティ（エクスプローラー互換）
                tab.ShowPropertiesCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                listBox.UnselectAll();
                e.Handled = true;
                break;
            case Key.Apps:
            case Key.F10 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                // アプリケーションキー / Shift+F10 で選択項目のシェルメニュー
                if (listBox.SelectedItem is FileSystemEntry sel && TryGetPlatformHandle() is { } handle)
                {
                    var pt = this.PointToScreen(listBox.TranslatePoint(new Point(80, 80), this) ?? new Point(200, 200));
                    var invoked = ShellContextMenuService.Show(handle.Handle, sel.FullPath, pt.X, pt.Y);
                    if (invoked)
                    {
                        DispatcherTimer.RunOnce(() => tab.NavigateTo(tab.CurrentPath, record: false), TimeSpan.FromMilliseconds(700));
                    }

                    e.Handled = true;
                }

                break;
            case Key.Enter:
                if (listBox.SelectedItem is FileSystemEntry entry)
                {
                    tab.Open(entry);
                    e.Handled = true;
                }

                break;
            case Key.Back:
                tab.GoUpCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ===== DnD（ドラッグ開始） =====

    private void FileList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox listBox || !e.GetCurrentPoint(listBox).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is not null)
        {
            _dragPressArgs = e;
            _dragListBox = listBox;
            _dragStartPoint = e.GetPosition(this);
            _dragInProgress = false;
        }
        else
        {
            // 背景クリックで選択解除（エクスプローラーと同じ）
            listBox.UnselectAll();
        }
    }

    private void FileList_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPressArgs is null || _dragInProgress || _dragListBox is null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStartPoint.X) < 6 && Math.Abs(pos.Y - _dragStartPoint.Y) < 6)
        {
            return;
        }

        if (_dragListBox.DataContext is not TabViewModel tab
            || _dragListBox.SelectedItems?.OfType<FileSystemEntry>().ToList() is not { Count: > 0 } selection)
        {
            return;
        }

        _dragInProgress = true;
        _ = StartDragAsync(tab, selection, _dragPressArgs);
    }

    private async Task StartDragAsync(TabViewModel tab, List<FileSystemEntry> selection, PointerPressedEventArgs pressArgs)
    {
        try
        {
            var transfer = new DataTransfer();
            foreach (var entry in selection)
            {
                IStorageItem? item = entry.IsDirectory
                    ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(entry.FullPath))
                    : await StorageProvider.TryGetFileFromPathAsync(new Uri(entry.FullPath));
                if (item is not null)
                {
                    transfer.Add(DataTransferItem.CreateFile(item));
                }
            }

            var effect = await DragDrop.DoDragDropAsync(pressArgs, transfer, DragDropEffects.Copy | DragDropEffects.Move);
            if (effect.HasFlag(DragDropEffects.Move))
            {
                DispatcherTimer.RunOnce(() => tab.NavigateTo(tab.CurrentPath, record: false), TimeSpan.FromMilliseconds(700));
            }
        }
        finally
        {
            _dragPressArgs = null;
            _dragListBox = null;
            _dragInProgress = false;
        }
    }

    // ===== DnD（ドロップ受け入れ） =====

    private static bool IsOnBookmarkBar(RoutedEventArgs e)
        => (e.Source as Visual)?.GetVisualAncestors()
            .OfType<Border>()
            .Any(b => b.Classes.Contains("bookmarkbar")) == true;

    /// <summary>ドロップ先ディレクトリの解決（タブバー / サイドバー / ファイル一覧の共通処理）。null = 対象外。</summary>
    private string? ResolveAnyDropTarget(DragEventArgs e, out TabViewModel? refreshTab)
    {
        refreshTab = null;
        var listBox = (e.Source as Visual)?.FindAncestorOfType<ListBox>();

        // タブバーへのドロップ = そのタブのフォルダーへ
        if (listBox?.Classes.Contains("tabs") == true)
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext
                is TabViewModel { IsSettingsTab: false } targetTab
                && targetTab.CurrentPath != FileSystemService.ComputerPath)
            {
                refreshTab = targetTab;
                return targetTab.CurrentPath;
            }

            return null;
        }

        // サイドバーのフォルダー項目へのドロップ
        if (listBox?.Classes.Contains("sidebar") == true)
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext
                is SidebarLink { IsShellCommand: false } link
                && link.Path != FileSystemService.ComputerPath
                && Directory.Exists(link.Path))
            {
                refreshTab = ViewModel?.SelectedTab;
                return link.Path;
            }

            return null;
        }

        // ファイル一覧
        if (listBox?.DataContext is TabViewModel tab && tab.CurrentPath != FileSystemService.ComputerPath)
        {
            refreshTab = tab;
            return ResolveDropTarget(e, tab);
        }

        return null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = GetDroppedPaths(e);
        if (files.Count == 0)
        {
            return;
        }

        if (IsOnBookmarkBar(e))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        if (ResolveAnyDropTarget(e, out _) is not { } dest)
        {
            return;
        }

        e.DragEffects = ResolveDropEffect(e, files, dest);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = GetDroppedPaths(e);
        if (files.Count == 0)
        {
            return;
        }

        // お気に入りバーへのドロップは登録（アプリ内 / アプリ外どちらの DnD も可）
        if (IsOnBookmarkBar(e))
        {
            e.Handled = true;
            foreach (var file in files)
            {
                ViewModel?.AddBookmark(file);
            }

            return;
        }

        if (ResolveAnyDropTarget(e, out var refreshTab) is not { } destDir
            || refreshTab is null)
        {
            return;
        }

        var move = ResolveDropEffect(e, files, destDir) == DragDropEffects.Move;
        e.Handled = true;
        _ = refreshTab.DropFilesAsync(files, destDir, move);
    }

    private static List<string> GetDroppedPaths(DragEventArgs e)
        => e.DataTransfer.TryGetFiles()?
               .Select(f => f.TryGetLocalPath())
               .Where(p => p is not null)
               .Select(p => p!)
               .ToList()
           ?? [];

    /// <summary>フォルダー行の上ならそのフォルダー、それ以外は現在のフォルダーへドロップ。</summary>
    private static string ResolveDropTarget(DragEventArgs e, TabViewModel tab)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        return item?.DataContext is FileSystemEntry { IsDirectory: true } entry
            ? entry.FullPath
            : tab.CurrentPath;
    }

    /// <summary>エクスプローラーと同じ既定則: 同一ドライブ = 移動 / 別ドライブ = コピー。Ctrl でコピー、Shift で移動を強制。</summary>
    private static DragDropEffects ResolveDropEffect(DragEventArgs e, List<string> files, string destDir)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return DragDropEffects.Copy;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return DragDropEffects.Move;
        }

        var sameRoot = string.Equals(
            Path.GetPathRoot(files[0]),
            Path.GetPathRoot(destDir),
            StringComparison.OrdinalIgnoreCase);
        return sameRoot ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    // ===== 右クリックメニュー =====

    private void FileList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragPressArgs = null;
        _dragListBox = null;
        _dragInProgress = false;

        if (sender is not ListBox { DataContext: TabViewModel tab } listBox)
        {
            return;
        }

        // 中クリックでフォルダーを新しいタブで開く（Chrome のリンク中クリック相当）
        if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>()?.DataContext
                is FileSystemEntry { IsDirectory: true } folder)
            {
                ViewModel?.OpenInNewTab(folder.FullPath);
                e.Handled = true;
            }

            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>();
        if (item?.DataContext is FileSystemEntry entry)
        {
            listBox.SelectedItem = entry;
            ShowShellContextMenu(tab, entry.FullPath, e);
        }
        else if (tab.CurrentPath != FileSystemService.ComputerPath)
        {
            // 背景も現在のフォルダーに対するWindows標準シェルメニューを直接表示する。
            ShowShellContextMenu(tab, tab.CurrentPath, e);
        }

        e.Handled = true;
    }

    /// <summary>追加機能はWindows標準コンテキストメニューを置き換えず、コマンドバーから提供する。</summary>
    private void AdvancedToolsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TabViewModel tab } button) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        if (tab.Selection.FirstOrDefault() is { } entry)
        {
            AddActionGroup(flyout, "選択項目: パス変換", ContextActionCatalog.For(ActionScope.File).Take(15),
                action => ExecuteFileContextActionAsync(action, tab, entry));
            AddActionGroup(flyout, "選択項目: 情報・ハッシュ", ContextActionCatalog.For(ActionScope.File).Skip(15).Take(10),
                action => ExecuteFileContextActionAsync(action, tab, entry));
            AddActionGroup(flyout, "選択項目: 開く", ContextActionCatalog.For(ActionScope.File).Skip(25).Take(8),
                action => ExecuteFileContextActionAsync(action, tab, entry));
            AddActionGroup(flyout, "選択項目: 整理", ContextActionCatalog.For(ActionScope.File).Skip(33),
                action => ExecuteFileContextActionAsync(action, tab, entry));
            flyout.Items.Add(new Separator());
        }
        AddActionGroup(flyout, "テンプレートから作成", ContextActionCatalog.For(ActionScope.Background).Take(10),
            action => ExecuteBackgroundActionAsync(action, tab));
        AddActionGroup(flyout, "現在の場所をコピー", ContextActionCatalog.For(ActionScope.Background).Skip(10).Take(5),
            action => ExecuteBackgroundActionAsync(action, tab));
        AddActionGroup(flyout, "この場所で開く", ContextActionCatalog.For(ActionScope.Background).Skip(15),
            action => ExecuteBackgroundActionAsync(action, tab));
        AddActionGroup(flyout, "条件を指定して選択", ContextActionCatalog.For(ActionScope.Selection),
            action => ExecuteSelectionAction(action, FindActiveFileList()));
        flyout.ShowAt(button);
    }

    private static void AddActionGroup(MenuFlyout flyout, string title, IEnumerable<ContextAction> actions,
        Func<ContextAction, Task> execute)
    {
        var group = new MenuItem { Header = title };
        foreach (var action in actions)
        {
            var item = new MenuItem { Header = action.Title };
            item.Click += async (_, _) => await execute(action);
            group.Items.Add(item);
        }
        flyout.Items.Add(group);
    }

    private async Task ExecuteFileContextActionAsync(ContextAction action, TabViewModel tab, FileSystemEntry entry)
    {
        var path = entry.FullPath;
        var parent = Path.GetDirectoryName(path) ?? tab.CurrentPath;
        string? clipboardText = action.Id switch
        {
            "file.copy-path" => path,
            "file.copy-quoted" => $"\"{path}\"",
            "file.copy-name" => entry.Name,
            "file.copy-stem" => Path.GetFileNameWithoutExtension(entry.Name),
            "file.copy-extension" => Path.GetExtension(entry.Name),
            "file.copy-parent" => parent,
            "file.copy-uri" => new Uri(path).AbsoluteUri,
            "file.copy-markdown" => $"[{EscapeMarkdown(entry.Name)}]({new Uri(path).AbsoluteUri})",
            "file.copy-html" => $"<a href=\"{System.Net.WebUtility.HtmlEncode(new Uri(path).AbsoluteUri)}\">{System.Net.WebUtility.HtmlEncode(entry.Name)}</a>",
            "file.copy-json" => QuoteJson(path),
            "file.copy-json-array" => $"[{string.Join(", ", tab.Selection.Select(x => QuoteJson(x.FullPath)))}]",
            "file.copy-powershell" => $"'{path.Replace("'", "''")}'",
            "file.copy-cmd" => $"\"{path.Replace("\"", "\"\"")}\"",
            "file.copy-slashes" => path.Replace('\\', '/'),
            "file.copy-wsl" => ToWslPath(path),
            "file.copy-size" => entry.Size is { } size ? $"{size} bytes ({FileSystemEntry.FormatSize(size)})" : "フォルダー",
            "file.copy-modified" => entry.Modified?.ToString("O") ?? "",
            "file.copy-created" => entry.Created?.ToString("O") ?? "",
            "file.copy-attributes" => SafeAttributes(path).ToString(),
            "file.copy-summary" => $"名前: {entry.Name}{Environment.NewLine}パス: {path}{Environment.NewLine}種類: {entry.TypeText}{Environment.NewLine}サイズ: {entry.SizeText}{Environment.NewLine}更新日時: {entry.ModifiedText}",
            "file.copy-tsv" => $"{entry.Name}\t{path}\t{entry.TypeText}\t{entry.Size?.ToString() ?? ""}\t{entry.Modified?.ToString("O") ?? ""}",
            "file.copy-getitem" => $"Get-Item -LiteralPath '{path.Replace("'", "''")}'",
            "file.copy-relative" => Path.GetRelativePath(tab.CurrentPath, path),
            _ => null,
        };
        if (clipboardText is not null)
        {
            await SetClipboardTextAsync(clipboardText, tab);
            return;
        }

        if (action.Id is "file.hash-sha256" or "file.hash-sha1" or "file.hash-md5")
        {
            if (entry.IsDirectory) { tab.StatusText = "フォルダーのハッシュは計算できません"; return; }
            tab.StatusText = "ハッシュを計算しています...";
            try
            {
                var hash = await Task.Run(() => ComputeHash(path, action.Id));
                await SetClipboardTextAsync(hash, tab);
            }
            catch (Exception ex) { tab.StatusText = $"ハッシュを計算できませんでした: {ex.Message}"; }
            return;
        }

        try
        {
            switch (action.Id)
            {
                case "file.open-parent": tab.NavigateTo(parent); break;
                case "file.open-parent-tab": ViewModel?.OpenInNewTab(parent); break;
                case "file.explorer-select": StartProcess("explorer.exe", ["/select,", path], parent); break;
                case "file.open-notepad": StartProcess("notepad.exe", [path], parent); break;
                case "file.terminal-here": StartProcess("wt.exe", ["-d", entry.IsDirectory ? path : parent], parent); break;
                case "file.powershell-here": StartProcess("powershell.exe", ["-NoExit", "-Command", $"Set-Location -LiteralPath '{(entry.IsDirectory ? path : parent).Replace("'", "''")}'"], parent); break;
                case "file.cmd-here": StartProcess("cmd.exe", ["/K", "cd", "/d", entry.IsDirectory ? path : parent], parent); break;
                case "file.vscode": StartProcess("code.cmd", [path], parent); break;
                case "file.duplicate":
                    if (entry.IsDirectory) FileOperationService.CopyOrMove([path], parent, move: false, renameOnCollision: true);
                    else File.Copy(path, UniqueSiblingPath(path, " - コピー"));
                    tab.RefreshCommand.Execute(null); break;
                case "file.backup":
                    var backup = UniqueSiblingPath(path, $" - バックアップ {DateTime.Now:yyyyMMdd-HHmmss}");
                    if (entry.IsDirectory) DirectoryCopy(path, backup); else File.Copy(path, backup);
                    tab.RefreshCommand.Execute(null); break;
                case "file.rename-lower": tab.CommitRename(entry, entry.Name.ToLowerInvariant()); break;
                case "file.rename-upper": tab.CommitRename(entry, entry.Name.ToUpperInvariant()); break;
                case "file.rename-underscores": tab.CommitRename(entry, entry.Name.Replace(' ', '_')); break;
                case "file.touch":
                    if (entry.IsDirectory) Directory.SetLastWriteTime(path, DateTime.Now); else File.SetLastWriteTime(path, DateTime.Now);
                    tab.RefreshCommand.Execute(null); break;
            }
        }
        catch (Exception ex) { tab.StatusText = $"操作を完了できませんでした: {ex.Message}"; }
    }

    private async Task ExecuteBackgroundActionAsync(ContextAction action, TabViewModel tab)
    {
        var path = tab.CurrentPath;
        string? text = action.Id switch
        {
            "bg.copy-path" => path, "bg.copy-quoted" => $"\"{path}\"", "bg.copy-uri" => new Uri(path).AbsoluteUri,
            "bg.copy-ps-cd" => $"Set-Location -LiteralPath '{path.Replace("'", "''")}'",
            "bg.copy-cmd-cd" => $"cd /d \"{path}\"", _ => null,
        };
        if (text is not null) { await SetClipboardTextAsync(text, tab); return; }
        try
        {
            if (action.Id.StartsWith("bg.create-", StringComparison.Ordinal))
            {
                var (name, content) = BackgroundTemplate(action.Id);
                var target = UniqueChildPath(path, name);
                await File.WriteAllTextAsync(target, content);
                tab.RefreshCommand.Execute(null);
                tab.StatusText = $"{Path.GetFileName(target)} を作成しました";
                return;
            }
            switch (action.Id)
            {
                case "bg.open-terminal": StartProcess("wt.exe", ["-d", path], path); break;
                case "bg.open-powershell": StartProcess("powershell.exe", ["-NoExit"], path); break;
                case "bg.open-cmd": StartProcess("cmd.exe", ["/K"], path); break;
                case "bg.open-vscode": StartProcess("code.cmd", [path], path); break;
                case "bg.open-notepad": StartProcess("notepad.exe", [], path); break;
            }
        }
        catch (Exception ex) { tab.StatusText = $"操作を完了できませんでした: {ex.Message}"; }
    }

    private Task ExecuteSelectionAction(ContextAction action, ListBox? list)
    {
        if (list is null) return Task.CompletedTask;
        var now = DateTime.Now;
        var extSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["select.images"] = ExtSet(".jpg;.jpeg;.png;.gif;.bmp;.webp;.tif;.tiff;.heic;.avif"),
            ["select.videos"] = ExtSet(".mp4;.mkv;.avi;.mov;.wmv;.webm;.m4v"),
            ["select.audio"] = ExtSet(".mp3;.wav;.flac;.aac;.ogg;.m4a;.wma"),
            ["select.documents"] = ExtSet(".txt;.md;.pdf;.doc;.docx;.xls;.xlsx;.ppt;.pptx;.odt;.ods"),
            ["select.archives"] = ExtSet(".zip;.7z;.rar;.tar;.gz;.bz2;.xz"),
            ["select.code"] = ExtSet(".cs;.cpp;.c;.h;.js;.ts;.py;.rs;.go;.java;.kt;.swift;.php;.rb;.vue;.svelte;.axaml"),
        };
        if (action.Id is "select.largest" or "select.smallest")
        {
            var ordered = list.Items.OfType<FileSystemEntry>().Where(x => x.Size is not null)
                .OrderBy(x => x.Size).ToList();
            if (action.Id == "select.largest") ordered.Reverse();
            SelectEntries(list, ordered.Take(10));
            return Task.CompletedTask;
        }
        SelectMatching(list, entry => action.Id switch
        {
            var id when extSets.TryGetValue(id, out var set) => !entry.IsDirectory && set.Contains(Path.GetExtension(entry.Name)),
            "select.hidden" => entry.IsHidden, "select.visible" => !entry.IsHidden,
            "select.empty" => entry.Size == 0, "select.nonempty" => entry.Size > 0,
            "select.today" => entry.Modified?.Date == now.Date,
            "select.week" => entry.Modified >= now.AddDays(-7), "select.old" => entry.Modified < now.AddDays(-30),
            "select.dotfiles" => entry.Name.StartsWith('.'), "select.spaces" => entry.Name.Contains(' '),
            "select.digits" => entry.Name.Any(char.IsDigit),
            "select.executable" => ExtSet(".exe;.com;.bat;.cmd;.ps1;.msi").Contains(Path.GetExtension(entry.Name)),
            "select.readonly" => SafeAttributes(entry.FullPath).HasFlag(FileAttributes.ReadOnly), _ => false,
        });
        return Task.CompletedTask;
    }

    private async Task SetClipboardTextAsync(string text, TabViewModel tab)
    {
        if (Clipboard is null) return;
        await Clipboard.SetTextAsync(text);
        tab.StatusText = "クリップボードにコピーしました";
    }

    private static string QuoteJson(string text) => $"\"{System.Text.Json.JsonEncodedText.Encode(text)}\"";
    private static string EscapeMarkdown(string text) => text.Replace("[", "\\[").Replace("]", "\\]");
    private static HashSet<string> ExtSet(string value) => value.Split(';').ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static FileAttributes SafeAttributes(string path) { try { return File.GetAttributes(path); } catch { return 0; } }

    private static string ToWslPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is { Length: >= 2 } && root[1] == ':')
            return $"/mnt/{char.ToLowerInvariant(root[0])}/{path[root.Length..].Replace('\\', '/')}";
        return path.Replace('\\', '/');
    }

    private static string ComputeHash(string path, string id)
    {
        using var stream = File.OpenRead(path);
        using System.Security.Cryptography.HashAlgorithm algorithm = id switch
        {
            "file.hash-sha256" => System.Security.Cryptography.SHA256.Create(),
            "file.hash-sha1" => System.Security.Cryptography.SHA1.Create(),
            _ => System.Security.Cryptography.MD5.Create(),
        };
        return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void StartProcess(string fileName, IEnumerable<string> args, string workingDirectory)
    {
        var info = new System.Diagnostics.ProcessStartInfo(fileName) { UseShellExecute = true, WorkingDirectory = workingDirectory };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        System.Diagnostics.Process.Start(info);
    }

    private static string UniqueSiblingPath(string source, string suffix)
    {
        var parent = Path.GetDirectoryName(source)!;
        var extension = Directory.Exists(source) ? "" : Path.GetExtension(source);
        var stem = Directory.Exists(source) ? Path.GetFileName(source) : Path.GetFileNameWithoutExtension(source);
        return UniqueChildPath(parent, stem + suffix + extension);
    }

    private static string UniqueChildPath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
        return candidate;
    }

    private static void DirectoryCopy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            DirectoryCopy(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static (string Name, string Content) BackgroundTemplate(string id) => id switch
    {
        "bg.create-txt" => ("新しいテキスト ドキュメント.txt", ""),
        "bg.create-md" => ("README.md", "# タイトル\n"),
        "bg.create-json" => ("data.json", "{\n  \"key\": \"value\"\n}\n"),
        "bg.create-yaml" => ("data.yaml", "key: value\n"),
        "bg.create-csv" => ("data.csv", "列1,列2\n"),
        "bg.create-html" => ("index.html", "<!doctype html>\n<html lang=\"ja\"><head><meta charset=\"utf-8\"><title></title></head><body></body></html>\n"),
        "bg.create-css" => ("style.css", "body {\n}\n"),
        "bg.create-js" => ("script.js", "'use strict';\n"),
        "bg.create-ps1" => ("script.ps1", "Set-StrictMode -Version Latest\n"),
        "bg.create-gitignore" => (".gitignore", "bin/\nobj/\n.vs/\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "未定義のテンプレートです"),
    };

    private static void SelectEntries(ListBox list, IEnumerable<FileSystemEntry> entries)
    {
        if (list.SelectedItems is not { } selected) return;
        selected.Clear();
        foreach (var entry in entries) selected.Add(entry);
    }

    /// <summary>指定パスに対する Windows 標準のシェルコンテキストメニューを表示する。</summary>
    private void ShowShellContextMenu(TabViewModel tab, string path, PointerReleasedEventArgs e)
        => ShowShellContextMenu(tab, path, this.PointToScreen(e.GetPosition(this)));

    private void ShowShellContextMenu(TabViewModel tab, string path, PixelPoint screen)
    {
        var handle = TryGetPlatformHandle();
        if (handle is null)
        {
            return;
        }

        var invoked = ShellContextMenuService.Show(handle.Handle, path, screen.X, screen.Y);
        if (invoked)
        {
            // 削除・貼り付けなどシェル側の処理完了を少し待ってから一覧を更新する
            DispatcherTimer.RunOnce(() => tab.NavigateTo(tab.CurrentPath, record: false), TimeSpan.FromMilliseconds(700));
        }
    }

    // ===== コマンドバー =====

    private void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            UpdateService.Check4Update(this, vm.Settings, manually: true);
        }
    }

    private void NewButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TabViewModel tab } button)
        {
            return;
        }

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        PopulateNewItems(flyout.Items, tab);
        flyout.ShowAt(button);
    }

    private static void PopulateNewItems(ItemCollection items, TabViewModel tab)
    {
        var folder = new MenuItem { Header = "フォルダー", Icon = new TextBlock { Text = "📁" } };
        folder.Click += (_, _) => tab.CreateNewFolder();
        items.Add(folder);

        items.Add(new Separator());

        foreach (var template in ShellNewService.GetTemplates())
        {
            var item = new MenuItem { Header = template.DisplayName, Icon = new TextBlock { Text = "📄" } };
            var captured = template;
            item.Click += (_, _) => tab.CreateFromTemplate(captured);
            items.Add(item);
        }
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
        => FindActiveFileList()?.SelectAll();

    private void SelectNone_Click(object? sender, RoutedEventArgs e)
        => FindActiveFileList()?.UnselectAll();

    private ListBox? FindActiveFileList()
        => this.GetVisualDescendants()
            .OfType<ListBox>()
            .FirstOrDefault(l => l.IsEffectivelyVisible
                                 && (l.Classes.Contains("files") || l.Classes.Contains("icons")));
}
