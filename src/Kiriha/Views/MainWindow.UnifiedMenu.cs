using Avalonia;
using Avalonia.Controls;
using Kiriha.Controls;
using Kiriha.Models;
using Kiriha.Services;
using Kiriha.ViewModels;

namespace Kiriha.Views;

/// <summary>右クリックメニューを出した場所。共通部に足す固有項目と、開き方の細かな違いを決める。</summary>
public enum ContextMenuSource
{
    /// <summary>ファイル一覧（このメニューが全体の基準）。</summary>
    FileList,
    QuickAccess,
    Tree,
    Bookmarks,
    AddressBar,
    Tab,
}

/// <summary>
/// 統一コンテキストメニューの「対象」。どの場所から開いても、共通部はこの情報だけで組み立てる。
/// </summary>
/// <param name="Source">開いた場所。固有項目の選択と、並び順（タブだけ固有が先）に使う。</param>
/// <param name="Paths">操作対象の実パス。空なら共通部を出さない（PC / 余白 / ごみ箱など）。</param>
/// <param name="ViewModel">タブ操作・お気に入り登録に使う。</param>
/// <param name="Screen">「その他のオプションを確認」で Win32 メニューを出す位置。</param>
internal sealed record ContextMenuTarget(
    ContextMenuSource Source,
    IReadOnlyList<string> Paths,
    MainWindowViewModel ViewModel,
    PixelPoint Screen)
{
    /// <summary>対象がすべてフォルダーだと呼び出し側が知っているか。null なら実際に調べる。</summary>
    public bool? AllDirectories { get; init; }

    /// <summary>ファイル一覧の右クリックで、対象が 1 件かつ現在の選択と一致するときの行。
    /// 「開く」をギャラリー対応の <see cref="TabViewModel.Open"/> に通すためだけに使う。</summary>
    public FileSystemEntry? Entry { get; init; }

    /// <summary>ファイル一覧の右クリックで、対象がタブの選択そのものか。名前の変更をインライン編集にする判定。</summary>
    public bool MatchesSelection { get; init; }

    /// <summary>ツリーの対象ノード（ツリー固有項目用）。</summary>
    public FolderTreeNode? TreeNode { get; init; }

    /// <summary>お気に入りの対象ノード（お気に入り固有項目用。複数選択あり）。</summary>
    public IReadOnlyList<BookmarkNode> BookmarkNodes { get; init; } = [];

    /// <summary>クイックアクセスの対象（ピン留め解除用）。</summary>
    public SidebarLink? SidebarLink { get; init; }

    /// <summary>タブ右クリックの対象タブ（タブ固有項目用）。</summary>
    public TabViewModel? ContextTab { get; init; }

    /// <summary>その場所の固有項目と重複するので落とすシェルの正規 verb。</summary>
    /// <remarks>
    /// 共通部が自前項目を足したときに落とす verb はビルダー側が登録するが、<b>固有項目</b>と重なる分は
    /// 場所しか知らない。クイックアクセスの pintohome（登録済みの行に「ピン留めする」が並ぶ）がその例。
    /// </remarks>
    public IReadOnlyCollection<string> ExcludedShellVerbs { get; init; } = [];
}

/// <summary>
/// 6 か所（ファイル一覧 / ツリー / お気に入り / クイックアクセス / アドレスバー / タブ）の
/// 右クリックメニューを 1 つの組み立てへ寄せた部分。
/// </summary>
/// <remarks>
/// <para>
/// <b>基準はファイル一覧のメニュー</b>で、他の場所はそこへ固有項目を足しただけにする。統一前は
/// 4 通りの作り方（自前描画 / Fluent MenuFlyout / Fluent ContextMenu / Win32）が並走し、
/// 「削除」「パスのコピー」が別々に 3〜4 回実装されていた。
/// </para>
/// <para>
/// 統一を阻んでいたのは見た目ではなく<b>対象の持ち方</b>だった。旧 <c>BuildModernMenu</c> の厳選項目は
/// 「右クリック対象＝タブの現在の選択」のときしか出せず（<see cref="SelectionMatches"/>）、
/// ツリーやお気に入りからは切り取り・名前の変更・削除が丸ごと落ちるため、各所が自前実装を持っていた。
/// いまは <see cref="ContextMenuTarget.Paths"/> を明示的に渡し、実行はパス指定版
/// （MainWindow.PathOperations.cs）を通すので、どこから開いても同じ結果になる。
/// </para>
/// <para>
/// <b>並び順</b>: 既定は「共通 → 固有」。タブだけ「固有 → 共通」にする（閉じる操作が主目的なので、
/// 共通部を先頭に置くと日常操作が遠くなるため）。
/// </para>
/// <para>
/// <b>設定 ContextMenuStyle</b> が効くのはファイル一覧だけ。Shell / System はシェルメニューそのものを
/// 見せるモードで、他の 5 か所はシェルメニューを持つ対象ですらないため、常にこの自前描画で出す。
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// 統一コンテキストメニューを表示する。
    /// </summary>
    /// <param name="anchor">表示位置の基準（この論理ツリーにメニューがぶら下がる）。</param>
    /// <param name="target">共通部の対象。null なら固有項目だけを出す（PC・余白など）。</param>
    /// <param name="ownEntries">その場所にしか無い項目。</param>
    /// <param name="screen">「その他のオプションを確認」で Win32 メニューを出す位置。</param>
    /// <param name="ownEntriesFirst">固有項目を先に並べる（タブのみ true）。</param>
    private void ShowUnifiedContextMenu(
        Control anchor,
        ContextMenuTarget? target,
        IReadOnlyList<ExplorerMenuEntry> ownEntries,
        PixelPoint screen,
        bool ownEntriesFirst = false)
    {
        var header = new List<ExplorerMenuEntry>();
        var common = new List<ExplorerMenuEntry>();
        ShellMenuSession? session = null;

        if (target is { Paths.Count: > 0 })
        {
            // シェル拡張の項目は実在パスにだけ意味がある。取れなくてもメニューは出す（自前項目だけになる）。
            if (TryGetPlatformHandle() is { } handle)
            {
                session = ShellMenuSession.Create(handle.Handle, target.Paths, extendedVerbs: false);
            }

            var excluded = new HashSet<string>(target.ExcludedShellVerbs, StringComparer.OrdinalIgnoreCase);
            BuildCommonHeaderRow(target, session, header, excluded);
            common.AddRange(BuildCommonPathEntries(target, excluded));

            if (session is not null)
            {
                var shellItems = ConvertShellItems(session, session.Items, target.Paths, excluded, thirdPartyOnly: true);
                if (shellItems.Count > 0)
                {
                    common.Add(ExplorerMenuEntry.Separator);
                    common.AddRange(shellItems);
                }
            }

            common.Add(ExplorerMenuEntry.Separator);
            common.Add(BuildShowMoreOptionsEntry(target, screen));
        }

        var items = new List<ExplorerMenuEntry>();
        if (ownEntriesFirst)
        {
            items.AddRange(ownEntries);
            if (ownEntries.Count > 0 && common.Count > 0)
            {
                items.Add(ExplorerMenuEntry.Separator);
            }

            items.AddRange(common);
        }
        else
        {
            items.AddRange(common);
            if (ownEntries.Count > 0 && common.Count > 0)
            {
                items.Add(ExplorerMenuEntry.Separator);
            }

            items.AddRange(ownEntries);
        }

        if (items.Count == 0 && header.Count == 0)
        {
            session?.Dispose();
            return;
        }

        var tab = target?.ViewModel.SelectedTab;
        ExplorerContextMenu.Show(
            anchor, items, header, session,
            tab is { IsSettingsTab: false } ? () => RefreshAfterShellOperation(tab) : null);
    }

    /// <summary>
    /// 上部の横並びアイコン行（切り取り / コピー / パスのコピー / 名前の変更 / 共有 / 削除 / プロパティ）。
    /// ファイル一覧の選択メニューと、他 5 か所の共通部で同じものを出す。
    /// </summary>
    /// <remarks>
    /// ドライブ直下（C:\）は <see cref="IsDriveRoot"/> で変更系を無効にする。IFileOperation へ渡すと
    /// 「中身の一括操作」になってしまうため、旧ツリーメニューが持っていた規則をここへ引き上げてある。
    /// </remarks>
    private void BuildCommonHeaderRow(
        ContextMenuTarget target,
        ShellMenuSession? session,
        List<ExplorerMenuEntry> header,
        HashSet<string> excludedVerbs)
    {
        var vm = target.ViewModel;
        var paths = target.Paths;
        var modifiable = paths.Where(p => !IsDriveRoot(p)).ToList();
        var canModify = modifiable.Count > 0;

        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Cut"),
            Glyph = GlyphCut,
            IsEnabled = canModify,
            Invoke = () => SetPathClipboard(vm, modifiable, cut: true),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.Copy"),
            Glyph = GlyphCopy,
            IsEnabled = canModify,
            Invoke = () => SetPathClipboard(vm, modifiable, cut: false),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.CopyPath"),
            Glyph = GlyphCopyPath,
            Invoke = () => _ = CopyPathsTextAsync(vm, paths),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Rename"),
            Glyph = GlyphRename,
            IsEnabled = modifiable.Count == 1,
            Invoke = () => InvokeRename(target),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Command.Share"),
            Glyph = GlyphShare,
            // フォルダーだけと分かっている対象では共有シートを開けない（Windows 側の制約）
            IsEnabled = target.AllDirectories != true,
            Invoke = () => SharePaths(vm, paths),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.Delete"),
            Glyph = GlyphDelete,
            IsEnabled = canModify,
            Invoke = () => _ = DeletePathsAsync(vm, modifiable),
        });
        header.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Common.Properties"),
            Glyph = GlyphProperties,
            Invoke = () => ShowPropertiesForPaths(session, paths),
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
    /// 共通部の本文（開く / 新しいタブ / ピン留め / お気に入り / 貼り付け / ターミナル / エクスプローラー）。
    /// フォルダー限定の項目は、対象が全部フォルダーのときだけ出す。
    /// </summary>
    private List<ExplorerMenuEntry> BuildCommonPathEntries(ContextMenuTarget target, HashSet<string> excludedVerbs)
    {
        var vm = target.ViewModel;
        var paths = target.Paths.ToList();
        var entries = new List<ExplorerMenuEntry>();

        // 「PC」やごみ箱のような仮想パスは実体が無いので、実ファイル向けの項目は出さない
        var allDirectories = target.AllDirectories ?? paths.All(Directory.Exists);

        // 既に excludedVerbs に入っている verb は「シェル側に任せる」の意味。Shell モードがこれを使い、
        // 「開く」「貼り付け」をシェルの項目のまま残して二重に並べないようにする。
        if (paths.Count == 1 && !excludedVerbs.Contains("open"))
        {
            excludedVerbs.Add("open");
            var single = paths[0];
            entries.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Common.Open"),
                Shortcut = target.Source == ContextMenuSource.FileList ? "Enter" : null,
                Glyph = GlyphOpen,
                IsDefault = true,
                Invoke = () => OpenTargetPath(target, single, allDirectories),
            });
        }

        if (allDirectories)
        {
            // シェルの「お気に入りに追加」(pintohomefile) はエクスプローラーの Home 用で、
            // Kiriha のお気に入りには出ない。同じ表示名が 2 つ並ぶのを避けてこちらを残す。
            excludedVerbs.Add("pintohomefile");

            entries.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Tab.OpenInTabs", paths.Count),
                Glyph = GlyphNewTab,
                Invoke = () => vm.OpenFolderTabs(paths),
            });
            entries.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Tab.PinInTabs", paths.Count),
                Glyph = GlyphPin,
                Invoke = () => vm.PinFolderTabs(paths),
            });
            // 全部が登録済みなら出さない（AddBookmark は重複を弾くので押しても何も起きず、
            // お気に入りペインを右クリックしたときに「追加」と「削除」が並んで意味不明になる）
            if (paths.Any(p => vm.FindBookmark(p) is null))
            {
                entries.Add(new ExplorerMenuEntry
                {
                    Text = LocalizationService.Text("Text.Common.AddToBookmarks"),
                    Glyph = GlyphFavorite,
                    Invoke = () =>
                    {
                        foreach (var path in paths)
                        {
                            vm.AddBookmark(path);
                        }
                    },
                });
            }

            // 貼り付け先が 1 つに定まるときだけ。背景メニューはアイコン行が持っているので二重には出さない。
            if (paths.Count == 1 && !excludedVerbs.Contains("paste"))
            {
                excludedVerbs.Add("paste");
                entries.Add(new ExplorerMenuEntry
                {
                    Text = LocalizationService.Text("Text.Command.Paste"),
                    Glyph = GlyphPaste,
                    IsEnabled = ClipboardFileService.HasFiles(),
                    Invoke = () => _ = PasteIntoFolderAsync(vm, paths[0]),
                });
            }

            excludedVerbs.UnionWith(WindowsTerminalVerbs);
            entries.Add(new ExplorerMenuEntry
            {
                Text = vm.SelectedTab is { } terminalTab
                    ? terminalTab.OpenTerminalText
                    : LocalizationService.Text("Text.Common.OpenInTerminal"),
                Glyph = GlyphTerminal,
                Invoke = () =>
                {
                    foreach (var path in paths)
                    {
                        TerminalLauncher.TryOpen(path);
                    }
                },
            });
            entries.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Common.OpenInExplorer"),
                Glyph = GlyphFolderOpen,
                Invoke = () =>
                {
                    foreach (var path in paths)
                    {
                        vm.SelectedTab?.OpenFolderInExplorer(path);
                    }
                },
            });
        }

        return entries;
    }

    /// <summary>末尾の「その他のオプションを確認」。Windows 標準の Win32 メニューへ逃がす。</summary>
    private ExplorerMenuEntry BuildShowMoreOptionsEntry(ContextMenuTarget target, PixelPoint screen)
        => new()
        {
            Text = LocalizationService.Text("Text.Menu.ShowMoreOptions"),
            Shortcut = target.Source == ContextMenuSource.FileList ? "Shift+F10" : null,
            Glyph = GlyphMore,
            Invoke = () =>
            {
                if (target.ViewModel.SelectedTab is { IsSettingsTab: false } tab)
                {
                    ShowSystemContextMenu(tab, target.Paths, screen);
                }
            },
        };

    /// <summary>「開く」の実処理。一覧からの単一選択だけはギャラリー対応の <see cref="TabViewModel.Open"/> を通す。</summary>
    private void OpenTargetPath(ContextMenuTarget target, string path, bool isDirectory)
    {
        if (target is { Source: ContextMenuSource.FileList, Entry: { } entry, ViewModel.SelectedTab: { } listTab })
        {
            listTab.Open(entry);
            return;
        }

        // お気に入りは実体が消えていることがあるので、行の選択で開いたときと同じ確認経路へ通す
        if (target is { Source: ContextMenuSource.Bookmarks, BookmarkNodes: [{ } bookmark] })
        {
            _ = ActivateBookmarkAsync(bookmark, path, target.ViewModel);
            return;
        }

        if (isDirectory)
        {
            target.ViewModel.SelectedTab?.NavigateTo(path);
            return;
        }

        OpenBookmarkFile(path);
    }

    /// <summary>
    /// 「名前の変更」。ファイル一覧で選択そのものを右クリックしたときだけインライン編集にし、
    /// それ以外はダイアログで変更する（一覧以外にはインライン編集の場が無いため）。
    /// </summary>
    private void InvokeRename(ContextMenuTarget target)
    {
        if (target is { Source: ContextMenuSource.FileList, MatchesSelection: true, ViewModel.SelectedTab: { } tab }
            && tab.RenameCommand.CanExecute(null))
        {
            tab.RenameCommand.Execute(null);
            return;
        }

        if (target.Paths.FirstOrDefault(p => !IsDriveRoot(p)) is { } path)
        {
            _ = RenamePathAsync(target.ViewModel, path);
        }
    }

    /// <summary>共有シート。フォルダーは Windows 側が受け付けないので、ファイルだけを渡す。</summary>
    private void SharePaths(MainWindowViewModel vm, IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            SetPathStatus(vm, LocalizationService.Text("Text.Share.FoldersNotSupported"));
            return;
        }

        var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (!ShareService.Show(hwnd, files))
        {
            SetPathStatus(vm, LocalizationService.Text("Text.Share.OpenFailed"));
        }
    }

    /// <summary>プロパティ表示。複数選択は SHMultiFileProperties を優先する（理由はセッション側の注記）。</summary>
    private static void ShowPropertiesForPaths(ShellMenuSession? session, IReadOnlyList<string> paths)
    {
        if (session is not null)
        {
            ShowPropertiesFor(session, paths);
            return;
        }

        if (paths.Count > 0)
        {
            FileOperationService.ShowProperties(paths[0]);
        }
    }
}
