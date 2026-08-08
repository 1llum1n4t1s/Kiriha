using Avalonia;
using Kiriha.Controls;
using Kiriha.Models;
using Kiriha.Services;
using Kiriha.ViewModels;

namespace Kiriha.Views;

/// <summary>
/// ファイル一覧の右クリックメニューのうち、Kiriha が自前で描画する分の組み立て。
/// </summary>
/// <remarks>
/// 設定 <see cref="ContextMenuStyle"/> で 3 通りに分かれる。
/// <list type="bullet">
/// <item><see cref="ContextMenuStyle.Modern"/>: Windows 11 のメニューに合わせ、上部のアイコン行と
/// 厳選した項目を Kiriha が自前で並べ、シェル側からは「Kiriha が用意しなかった項目」だけを取り込む。
/// 末尾の「その他のオプションを確認」で Win32 メニューへ逃がせる。</item>
/// <item><see cref="ContextMenuStyle.Shell"/>: シェルの項目をそのまま Windows 11 風の見た目で並べる。</item>
/// <item><see cref="ContextMenuStyle.System"/>: 自前描画せず Win32 の TrackPopupMenuEx をそのまま使う。</item>
/// </list>
/// <para>
/// Modern で「シェル側の何を落とすか」は正規 verb 名で判定し、しかも
/// <c>excludedVerbs</c> へは Kiriha が実際に自前項目を足したときだけ登録する。表示名で判定すると
/// 17 言語ぶんの対応表が要るうえ、条件で自前項目を出さなかったときに機能ごと消えてしまう。
/// </para>
/// </remarks>
public partial class MainWindow
{
    // Segoe Fluent Icons のグリフ。アイコン行と自前項目の左アイコンに使う。
    private const string GlyphCut = "\uE8C6";
    private const string GlyphCopy = "\uE8C8";
    private const string GlyphRename = "\uE8AC";
    private const string GlyphShare = "\uE72D";
    private const string GlyphDelete = "\uE74D";
    private const string GlyphOpen = "\uE8E5";
    private const string GlyphNewTab = "\uE8A7";
    private const string GlyphPin = "\uE718";
    private const string GlyphFolderOpen = "\uE838";
    private const string GlyphFavorite = "\uE734";
    private const string GlyphCopyPath = "\uE71B";
    private const string GlyphProperties = "\uE946";
    private const string GlyphMore = "\uE712";
    private const string GlyphRefresh = "\uE72C";
    private const string GlyphPaste = "\uE77F";
    // 「すべて選択」は MultiSelect (E9D5)。SelectAll (E8B3) は四隅の括弧と点の集合なので、
    // メニューの実寸（13px）で描くと QR コードにしか見えない。
    // E9D5 は Segoe Fluent Icons と Segoe MDL2 Assets の両方にあるため、
    // Fluent を持たない Windows 10 でも豆腐にならない（両フォントで描画を確認済み）。
    private const string GlyphSelectAll = "\uE9D5";
    private const string GlyphNewFolder = "\uE8F4";
    private const string GlyphSort = "\uE8CB";
    private const string GlyphView = "\uE890";
    private const string GlyphTerminal = "\uE756";
    // \u30BF\u30D6\u306E\u9589\u3058\u308B\u30DC\u30BF\u30F3\uFF08MainWindow.axaml \u306E tabclose\uFF09\u3068\u540C\u3058\u5B57\u306B\u305D\u308D\u3048\u308B
    private const string GlyphClose = "\uE8BB";

    /// <summary>
    /// Windows Terminal がシェルへ足す「ターミナルで開く」の正規 verb。Kiriha 側の同名項目を出すときは
    /// こちらを落とす（同じ表示名が 2 行並ぶうえ、設定「ターミナルを管理者として開く」が効くのは
    /// Kiriha の項目だけなので、どちらを押したかで挙動が変わってしまう）。
    /// </summary>
    /// <remarks>
    /// Windows Terminal は IExplorerCommand のパッケージ登録型ハンドラーで、レジストリの
    /// Directory\shell には出てこない。ただし GetCommandString(GCS_VERBW) はハンドラーの CLSID を返す
    /// —— 実測（このリポジトリのフォルダーを対象に QueryContextMenu を 2 回）で、表示名
    /// 「ターミナルで開く(&amp;T)」の verb が {9F156763-...} であることを確認した。表示名は Windows の
    /// UI 言語で変わり、しかも Kiriha のロケール設定とは一致しないので、判定は必ずこの CLSID で行う。
    /// 2 つ目は Windows Terminal Preview の CLSID（この環境には未インストールで未実測。外れていても
    /// 「Preview の項目が残る」だけで害は無い）。
    /// </remarks>
    private static readonly HashSet<string> WindowsTerminalVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "{9F156763-7844-4DC4-B2B1-901F640F5155}",
        "{02DB545A-3E20-46DE-83A5-1329B1E88B6B}",
    };

    /// <summary>
    /// Windows 自身がシェルメニューへ足す項目の正規 verb。Modern モードでは
    /// <see cref="KeptWindowsVerbs"/> に無いものを落とす（Windows 11 のメニューも出さないため）。
    /// サードパーティのシェル拡張は verb が GUID か独自名なのでこの一覧に載らず、生き残る。
    /// </summary>
    private static readonly HashSet<string> WindowsShellVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "open", "opennewwindow", "opennewprocess", "explore", "find", "print", "printto", "runas",
        "cut", "copy", "paste", "delete", "rename", "link", "properties", "openas", "copyaspath",
        "pintohome", "unpinfromhome", "pintohomefile", "pintostartscreen", "unpintostartscreen",
        "taskbarpin", "taskbarunpin", "PreviousVersions", "Windows.Share", "Windows.ModernShare",
        "restore", "undelete", "eject", "format", "mount", "burn", "sharing", "givetoall",
    };

    /// <summary>Windows 11 のメニューにも並ぶので Modern でも残す Windows 標準 verb。</summary>
    private static readonly HashSet<string> KeptWindowsVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "openas", "pintohome", "pintostartscreen", "pintohomefile",
    };

    /// <summary>
    /// 自前描画のコンテキストメニューを表示する。表示できたら true。
    /// シェルメニューを組み立てられなかったときは false を返し、呼び出し側が Win32 表示へ落とす。
    /// </summary>
    private bool TryShowCustomContextMenu(
        TabViewModel tab,
        IReadOnlyList<string> paths,
        ContextMenuStyle style,
        bool extendedVerbs,
        PixelPoint screen,
        Rect? anchor)
    {
        if (TryGetPlatformHandle() is not { } handle)
        {
            return false;
        }

        var session = ShellMenuSession.Create(handle.Handle, paths, extendedVerbs);
        if (session is null)
        {
            return false;
        }

        try
        {
            var header = new List<ExplorerMenuEntry>();
            var items = style == ContextMenuStyle.Shell
                ? BuildShellStyleMenu(tab, session, paths, screen)
                : BuildModernMenu(tab, session, paths, screen, header);

            if (items.Count == 0)
            {
                session.Dispose();
                return false;
            }

            ExplorerContextMenu.Show(this, items, header, session, () => RefreshAfterShellOperation(tab), anchor);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogException("コンテキストメニューの組み立てに失敗しました", ex);
            session.Dispose();
            return false;
        }
    }

    /// <summary>シェルの項目をそのまま並べるモード。末尾に Kiriha 独自の項目だけ足す。</summary>
    private List<ExplorerMenuEntry> BuildShellStyleMenu(
        TabViewModel tab,
        ShellMenuSession session,
        IReadOnlyList<string> paths,
        PixelPoint screen)
    {
        // このモードはシェルの項目をそのまま並べるのが約束なので、原則 excludedVerbs は使わない。
        // 「開く」「貼り付け」は先に入れておくことで共通ビルダー側が自前項目を出さず、シェルの項目が残る。
        // 例外は Windows Terminal だけ —— Kiriha 側の「ターミナルで開く」と同じ表示名が 2 行並び、
        // どちらを押したかで昇格するかが変わってしまう。表示名の違う重複（お気に入りに追加など）は従来どおり残す。
        var kirihaVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "open", "paste" };
        var extras = BuildCommonPathEntries(
            CreateFileListTarget(tab, paths, SelectionMatches(tab, paths), screen), kirihaVerbs);
        kirihaVerbs.IntersectWith(WindowsTerminalVerbs);

        var items = ConvertShellItems(session, session.Items, paths, kirihaVerbs, thirdPartyOnly: false);
        if (extras.Count > 0)
        {
            items.Add(ExplorerMenuEntry.Separator);
            items.AddRange(extras);
        }

        return items;
    }

    /// <summary>Windows 11 相当のモード。自前項目 → シェル拡張 → その他のオプション、の順で並べる。</summary>
    private List<ExplorerMenuEntry> BuildModernMenu(
        TabViewModel tab,
        ShellMenuSession session,
        IReadOnlyList<string> paths,
        PixelPoint screen,
        List<ExplorerMenuEntry> header)
    {
        // 自前項目が「選択中の項目」を対象にできるのは、右クリック対象が今の選択と一致するときだけ。
        // 背景クリック（対象は現在のフォルダー自身）はアイコン行の中身が変わる。
        var usesSelection = SelectionMatches(tab, paths);
        var excludedVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ExplorerMenuEntry>();
        var target = CreateFileListTarget(tab, paths, usesSelection, screen);

        if (usesSelection)
        {
            // アイコン行と本文は他の 5 か所と同じ組み立てを通す（MainWindow.UnifiedMenu.cs）
            BuildCommonHeaderRow(target, session, header, excludedVerbs);
        }
        else
        {
            BuildBackgroundHeaderRow(tab, session, paths, header, excludedVerbs);
        }

        // 背景の右クリックは「今のフォルダーの見せ方」を変える場所でもあるので、
        // コマンドバーと同じ 並べ替え / 表示 をサブメニューとして出す（エクスプローラーの背景メニューと同じ並び）。
        if (!usesSelection)
        {
            items.Add(BuildSortSubMenu(tab));
            items.Add(BuildViewSubMenu(tab));
            items.Add(ExplorerMenuEntry.Separator);
        }

        items.AddRange(BuildCommonPathEntries(target, excludedVerbs));

        // 「タスクバーにピン留めする」はここには置けない。Windows 11 のそれは Explorer 内部の実装で、
        // 公開シェル API（IContextMenu / IExplorerCommand）には出てこない —— 実測でも、フォルダーにも
        // exe にも taskbarpin verb は現れず、出るのは「スタート にピン留めする」(pintostartscreen) と
        // 「クイック アクセスにピン留めする」(pintohome) だけだった。どちらもシェル項目として
        // そのまま生き残る（KeptWindowsVerbs）ので、自前項目を足す必要はない。

        // シェル側からは「サードパーティ拡張の、直接実行できる項目」だけを取り込む。
        // Windows 自身の項目（ショートカットの作成・以前のバージョンの復元 等）と、
        // すべてのサブメニュー（送る・アクセスを許可する 等）はここでは出さない。
        var shellItems = ConvertShellItems(session, session.Items, paths, excludedVerbs, thirdPartyOnly: true);
        if (shellItems.Count > 0)
        {
            items.Add(ExplorerMenuEntry.Separator);
            items.AddRange(shellItems);
        }

        items.Add(ExplorerMenuEntry.Separator);
        items.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Menu.ShowMoreOptions"),
            Shortcut = "Shift+F10",
            Glyph = GlyphMore,
            Invoke = () => ShowSystemContextMenu(tab, paths, screen),
        });

        return items;
    }

    /// <summary>ファイル一覧の右クリック対象を、統一メニュー（MainWindow.UnifiedMenu.cs）の形へ包む。</summary>
    /// <param name="usesSelection">右クリック対象が今の選択と一致するか（背景クリックなら false）。</param>
    /// <remarks>
    /// <see cref="ContextMenuTarget.Entry"/> は単一選択のときだけ入れる。「開く」をギャラリー対応の
    /// <see cref="TabViewModel.Open"/> に通すためで、他の 5 か所には対応する行が無い。
    /// </remarks>
    private ContextMenuTarget CreateFileListTarget(
        TabViewModel tab,
        IReadOnlyList<string> paths,
        bool usesSelection,
        PixelPoint screen)
        => new(ContextMenuSource.FileList, paths, ViewModel!, screen)
        {
            MatchesSelection = usesSelection,
            Entry = usesSelection && tab.Selection.Count == 1 ? tab.Selection[0] : null,
        };

    /// <summary>
    /// 背景（何も無いところ）を右クリックしたときの、上部の横並びアイコン行
    /// （貼り付け / すべて選択 / 新規フォルダー / 最新の情報に更新 / プロパティ）。
    /// </summary>
    /// <remarks>
    /// 選択時のアイコン行がエクスプローラーの並びをなぞっているのに対し、こちらは Kiriha 独自。
    /// Windows 11 の背景メニューにアイコン行は無いので手本が無く、
    /// <b>「選択が要らない・引数も要らない・すぐ効く」操作だけ</b>を入れる方針で選んである。
    /// 並べ替え / 表示は子メニューを開かないと決まらないのでアイコンにできず、本文側に残る。
    /// ここへ入れた操作は本文から取り除く（同じ機能が 2 行に並ぶのを避ける。選択時と同じ扱い）。
    /// </remarks>
    private void BuildBackgroundHeaderRow(
        TabViewModel tab,
        ShellMenuSession session,
        IReadOnlyList<string> paths,
        List<ExplorerMenuEntry> header,
        HashSet<string> excludedVerbs)
    {
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Paste"),
            Glyph = GlyphPaste,
            IsEnabled = tab.PasteCommand.CanExecute(null),
            Invoke = () => tab.PasteCommand.Execute(null),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Select.All"),
            Glyph = GlyphSelectAll,
            Invoke = () => FindActiveFileList()?.SelectAll(),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.NewFolder"),
            Glyph = GlyphNewFolder,
            IsEnabled = tab.CanCreateNew,
            Invoke = tab.CreateNewFolder,
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Refresh"),
            Glyph = GlyphRefresh,
            Invoke = () => tab.RefreshCommand.Execute(null),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.Properties"),
            Glyph = GlyphProperties,
            Invoke = () => ShowPropertiesFor(session, paths),
        });

        excludedVerbs.Add("paste");
        excludedVerbs.Add("properties");
    }

    /// <summary>背景メニューの「並べ替え」。コマンドバーの並べ替えボタンと同じ中身。</summary>
    private static ExplorerMenuEntry BuildSortSubMenu(TabViewModel tab)
    {
        ExplorerMenuEntry SortKeyEntry(string textKey, string sortKey, bool isChecked) => new()
        {
            Text = LocalizationService.Text(textKey),
            IsChecked = isChecked,
            Invoke = () => tab.SetSortKeyCommand.Execute(sortKey),
        };

        return new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Sort"),
            Glyph = GlyphSort,
            Children =
            [
                SortKeyEntry("Text.Column.Name", SortKeys.Name, tab.IsSortByName),
                SortKeyEntry("Text.Column.Modified", SortKeys.Modified, tab.IsSortByModified),
                SortKeyEntry("Text.Column.Created", SortKeys.Created, tab.IsSortByCreated),
                SortKeyEntry("Text.Column.Type", SortKeys.Type, tab.IsSortByType),
                SortKeyEntry("Text.Column.Size", SortKeys.Size, tab.IsSortBySize),
                ExplorerMenuEntry.Separator,
                new ExplorerMenuEntry
                {
                    Text = LocalizationService.Text("Text.Sort.Ascending"),
                    IsChecked = tab.IsSortAscending,
                    Invoke = () => tab.SetSortAscendingCommand.Execute("True"),
                },
                new ExplorerMenuEntry
                {
                    Text = LocalizationService.Text("Text.Sort.Descending"),
                    IsChecked = tab.IsSortDescending,
                    Invoke = () => tab.SetSortAscendingCommand.Execute("False"),
                },
            ],
        };
    }

    /// <summary>背景メニューの「表示」。表示方法の選択だけ（「PC」は並べて表示に固定なので無効化する）。</summary>
    private static ExplorerMenuEntry BuildViewSubMenu(TabViewModel tab)
    {
        var canChange = tab.CanChangeViewMode;
        ExplorerMenuEntry ViewEntry(string textKey, ViewMode mode, bool isChecked) => new()
        {
            Text = LocalizationService.Text(textKey),
            IsChecked = isChecked,
            IsEnabled = canChange,
            Invoke = () => tab.SetViewModeCommand.Execute(mode.ToString()),
        };

        return new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.View"),
            Glyph = GlyphView,
            Children =
            [
                ViewEntry("Text.View.ExtraLarge", ViewMode.ExtraLargeIcons, tab.IsViewExtraLarge),
                ViewEntry("Text.View.Large", ViewMode.LargeIcons, tab.IsViewLarge),
                ViewEntry("Text.View.Medium", ViewMode.MediumIcons, tab.IsViewMedium),
                ViewEntry("Text.View.Small", ViewMode.SmallIcons, tab.IsViewSmall),
                ViewEntry("Text.View.List", ViewMode.List, tab.IsViewList),
                ViewEntry("Text.View.Details", ViewMode.Details, tab.IsViewDetails),
                ViewEntry("Text.View.Tiles", ViewMode.Tiles, tab.IsViewTiles),
                ViewEntry("Text.View.Gallery", ViewMode.Gallery, tab.IsViewGallery),
            ],
        };
    }

    /// <summary>吸い出したシェル項目を表示用モデルへ変換する（サブメニューは再帰）。</summary>
    /// <param name="excludedVerbs">落とす正規 verb 名。null なら何も落とさない。</param>
    /// <param name="thirdPartyOnly">
    /// Modern モード。サードパーティ拡張の項目だけを残し、Windows 自身の項目
    /// （<see cref="KeptWindowsVerbs"/> を除く）とサブメニューを落とす。
    /// <para>
    /// サブメニューを一律で落としているのは、<b>Windows のサブメニューと拡張のサブメニューを
    /// 区別する手掛かりがないから</b>。実測したところ「送る」「アクセスを許可する」の子も
    /// サードパーティ（proCertum SmartSign）の子も verb を持たず、コマンド ID の範囲も重なる。
    /// 誤って Windows 側を残すより、まとめて「その他のオプションを確認」へ委ねるほうが
    /// 短く予測しやすいメニューになる（Windows 11 自身もサブメニューはそちら側へ寄せている）。
    /// </para>
    /// </param>
    private static List<ExplorerMenuEntry> ConvertShellItems(
        ShellMenuSession session,
        IReadOnlyList<ShellMenuItem> source,
        IReadOnlyList<string> paths,
        HashSet<string>? excludedVerbs,
        bool thirdPartyOnly)
    {
        var list = new List<ExplorerMenuEntry>(source.Count);
        foreach (var item in source)
        {
            if (item.IsSeparator)
            {
                list.Add(ExplorerMenuEntry.Separator);
                continue;
            }

            if (excludedVerbs is not null && item.Verb is { Length: > 0 } verb && excludedVerbs.Contains(verb))
            {
                continue;
            }

            if (thirdPartyOnly && !IsThirdPartyShellItem(item))
            {
                continue;
            }

            var children = item.Children.Count > 0
                ? ConvertShellItems(session, item.Children, paths, excludedVerbs, thirdPartyOnly)
                : [];

            // 中身を全部間引いたサブメニューは出さない
            if (item.Children.Count > 0 && children.All(c => c.IsSeparator))
            {
                continue;
            }

            list.Add(new ExplorerMenuEntry
            {
                Text = item.Text,
                Shortcut = item.Shortcut,
                Image = item.Icon,
                IsEnabled = item.IsEnabled,
                IsDefault = item.IsDefault,
                IsChecked = item.IsChecked,
                Children = children,
                Invoke = children.Count > 0 ? null : CreateShellInvoke(session, item, paths),
            });
        }

        // 間引きで生じた先頭・末尾・連続の区切り線は ExplorerContextMenu 側で畳まれる
        return list;
    }

    /// <summary>
    /// Modern モードで残す価値のあるシェル項目か。サブメニューは持ち主を判別できないので落とし、
    /// Windows 標準の verb も <see cref="KeptWindowsVerbs"/> 以外は落とす。
    /// </summary>
    private static bool IsThirdPartyShellItem(ShellMenuItem item)
    {
        if (item.Children.Count > 0 || item.CommandId <= 0)
        {
            return false;
        }

        return item.Verb is not { Length: > 0 } verb
               || !WindowsShellVerbs.Contains(verb)
               || KeptWindowsVerbs.Contains(verb);
    }

    private static Action? CreateShellInvoke(ShellMenuSession session, ShellMenuItem item, IReadOnlyList<string> paths)
    {
        if (item.CommandId <= 0)
        {
            return null;
        }

        var commandId = item.CommandId;
        var multiProperties = paths.Count > 1
                              && string.Equals(item.Verb, "properties", StringComparison.OrdinalIgnoreCase);
        return () =>
        {
            if (multiProperties && session.TryShowMultiFileProperties())
            {
                return;
            }

            session.Invoke(commandId);
        };
    }

    /// <summary>プロパティ表示。複数選択は SHMultiFileProperties を優先する（理由はセッション側の注記）。</summary>
    private static void ShowPropertiesFor(ShellMenuSession session, IReadOnlyList<string> paths)
    {
        if (paths.Count > 1 && session.TryShowMultiFileProperties())
        {
            return;
        }

        if (!session.InvokeVerb("properties") && paths.Count > 0)
        {
            FileOperationService.ShowProperties(paths[0]);
        }
    }

    /// <summary>右クリック対象が「今の選択そのもの」かどうか。自前項目を選択ベースで動かしてよいかの判定。</summary>
    private static bool SelectionMatches(TabViewModel tab, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || tab.Selection.Count != paths.Count)
        {
            return false;
        }

        var selected = new HashSet<string>(
            tab.Selection.Select(s => s.FullPath), WindowsPathIdentity.Instance);
        return paths.All(selected.Contains);
    }
}
