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
                ? BuildShellStyleMenu(tab, session, paths)
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
        IReadOnlyList<string> paths)
    {
        var items = ConvertShellItems(session, session.Items, paths, excludedVerbs: null, thirdPartyOnly: false);
        var extras = BuildKirihaFolderEntries(tab, paths);
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
        // 背景クリック（対象は現在のフォルダー自身）では選択が別物なので、シェル項目へ任せる。
        var usesSelection = SelectionMatches(tab, paths);
        var excludedVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ExplorerMenuEntry>();

        if (usesSelection)
        {
            BuildHeaderRow(tab, session, paths, header, excludedVerbs);
        }

        if (usesSelection && tab.Selection.Count == 1)
        {
            var entry = tab.Selection[0];
            excludedVerbs.Add("open");
            items.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Common.Open"),
                Shortcut = "Enter",
                Glyph = GlyphOpen,
                IsDefault = true,
                Invoke = () => tab.Open(entry),
            });
        }

        // 背景の右クリックにはエクスプローラーと同じ「最新の情報に更新」を先頭側へ置く
        // （項目への右クリックでは意味が薄いので背景だけ。System モードのシェル既定メニューには
        // シェル自身の更新項目が無いため、Modern のここで補う）。
        if (!usesSelection)
        {
            items.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Command.Refresh"),
                Glyph = GlyphRefresh,
                Invoke = () => tab.RefreshCommand.Execute(null),
            });
        }

        items.AddRange(BuildKirihaFolderEntries(tab, paths, excludedVerbs));

        // 選択に対する パスのコピー / プロパティ は上部のアイコン行が持つ（BuildHeaderRow）。
        // 背景の右クリックにはアイコン行が無いので、そのときだけ従来どおり本文へ並べる。
        if (!usesSelection)
        {
            excludedVerbs.Add("properties");
            items.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Common.Properties"),
                Shortcut = "Alt+Enter",
                Glyph = GlyphProperties,
                Invoke = () => ShowPropertiesFor(session, paths),
            });
        }

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

    /// <summary>
    /// 上部の横並びアイコン行
    /// （切り取り / コピー / パスのコピー / 名前の変更 / 共有 / 削除 / プロパティ）。
    /// </summary>
    private void BuildHeaderRow(
        TabViewModel tab,
        ShellMenuSession session,
        IReadOnlyList<string> paths,
        List<ExplorerMenuEntry> header,
        HashSet<string> excludedVerbs)
    {
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Cut"),
            Glyph = GlyphCut,
            IsEnabled = tab.CutCommand.CanExecute(null),
            Invoke = () => tab.CutCommand.Execute(null),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.Copy"),
            Glyph = GlyphCopy,
            IsEnabled = tab.CopyCommand.CanExecute(null),
            Invoke = () => tab.CopyCommand.Execute(null),
        });
        // Windows 11 の「パスのコピー」（copyaspath）と同じ機能。Kiriha 側は Ctrl+Shift+C と
        // ステータスバー通知が付くので、こちらを出してシェル側は落とす。
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.CopyPath"),
            Glyph = GlyphCopyPath,
            Invoke = () => CopySelectedPaths(tab),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Rename"),
            Glyph = GlyphRename,
            IsEnabled = tab.RenameCommand.CanExecute(null),
            Invoke = () => tab.RenameCommand.Execute(null),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Share"),
            Glyph = GlyphShare,
            Invoke = () => tab.ShareCommand.Execute(null),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.Delete"),
            Glyph = GlyphDelete,
            IsEnabled = tab.DeleteCommand.CanExecute(null),
            Invoke = () => tab.DeleteCommand.Execute(null),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.Properties"),
            Glyph = GlyphProperties,
            Invoke = () => ShowPropertiesFor(session, paths),
        });

        // 自前で用意した以上、シェル側の同じ機能は重複させない
        excludedVerbs.Add("cut");
        excludedVerbs.Add("copy");
        excludedVerbs.Add("copyaspath");
        excludedVerbs.Add("rename");
        excludedVerbs.Add("delete");
        excludedVerbs.Add("properties");
        excludedVerbs.Add("Windows.Share");
        excludedVerbs.Add("Windows.ModernShare");
    }

    /// <summary>
    /// フォルダーを右クリックしたときだけ足す Kiriha 独自の項目。
    /// Win32 メニュー側の <c>BuildOpenInTabsItem</c> 等と同じ条件・同じ動作にそろえてある。
    /// </summary>
    /// <param name="excludedVerbs">
    /// 足した項目と機能が重なるシェル verb をここへ登録する（Modern モードのみ。Shell モードは null）。
    /// </param>
    private List<ExplorerMenuEntry> BuildKirihaFolderEntries(
        TabViewModel tab,
        IReadOnlyList<string> paths,
        HashSet<string>? excludedVerbs = null)
    {
        var entries = new List<ExplorerMenuEntry>();
        if (ViewModel is not { } vm || paths.Count == 0 || !paths.All(Directory.Exists))
        {
            return entries;
        }

        // シェルの「お気に入りに追加」(pintohomefile) はエクスプローラーの Home に足すもので、
        // Kiriha のサイドバーには現れない。同じ表示名が 2 つ並ぶのを避けて、こちらを残す。
        excludedVerbs?.Add("pintohomefile");

        var targets = paths.ToList();
        entries.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Tab.OpenInTabs", targets.Count),
            Glyph = GlyphNewTab,
            Invoke = () => vm.OpenFolderTabs(targets),
        });
        entries.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Tab.PinInTabs", targets.Count),
            Glyph = GlyphPin,
            Invoke = () => vm.PinFolderTabs(targets),
        });
        entries.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.AddToBookmarks"),
            Glyph = GlyphFavorite,
            Invoke = () =>
            {
                foreach (var path in targets)
                {
                    vm.AddBookmark(path);
                }
            },
        });
        entries.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.OpenInExplorer"),
            Glyph = GlyphFolderOpen,
            Invoke = () =>
            {
                foreach (var path in targets)
                {
                    tab.OpenFolderInExplorer(path);
                }
            },
        });
        return entries;
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
