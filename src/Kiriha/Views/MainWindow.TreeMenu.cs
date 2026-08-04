using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kiriha.Models;
using Kiriha.Services;
using Kiriha.ViewModels;

namespace Kiriha.Views;

/// <summary>
/// 左ペインのフォルダーツリーの右クリックメニュー。
/// </summary>
/// <remarks>
/// ファイル一覧の右クリック（ShowShellContextMenu）と違い、ツリーは「選択＝そのタブの移動」なので、右クリックで選択を動かさずにノードのパスだけを対象にする。
/// そのため各項目はタブの選択ベースのコマンドを使わず、パスを直接受け取る形で実装している。
/// 一覧のシェルメニューをそのまま出さないのは、Modern モードの自前項目が「右クリック対象＝現在の選択」の
/// ときしか出ないため、ツリーからだと切り取り・名前の変更・削除が丸ごと落ちてしまうから。
/// 代わりに末尾の「その他のオプションを確認」で Windows 標準メニューへ逃がしてある。
/// </remarks>
public partial class MainWindow
{
    /// <summary>ツリーの右クリック。ノード上ならそのフォルダー、余白ならツリー全体の操作を出す。</summary>
    private void SidebarTree_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TreeView tree || e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        // 右ボタンドラッグを終えた直後のリリースではメニューを出さない（一覧・左ペインと同じ規則）
        if (_suppressContextMenuAfterDrag)
        {
            _suppressContextMenuAfterDrag = false;
            e.Handled = true;
            return;
        }

        if (ViewModel is not { } vm)
        {
            return;
        }

        var node = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext as FolderTreeNode;
        if (node is NewTreeItemNode)
        {
            // 入力中の行はメニューの対象にしない（確定・取り消しの操作を邪魔しないため）
            return;
        }

        var screen = this.PointToScreen(e.GetPosition(this));
        var flyout = new MenuFlyout();
        BuildSidebarTreeMenu(flyout, vm, node, screen);
        if (flyout.Items.Count > 0)
        {
            flyout.ShowAt(tree, showAtPointer: true);
        }

        e.Handled = true;
    }

    /// <summary>ツリーの右クリックメニューを組み立てる。<paramref name="node"/> が null なら余白のクリック。</summary>
    private void BuildSidebarTreeMenu(
        MenuFlyout flyout, MainWindowViewModel vm, FolderTreeNode? node, PixelPoint screen)
    {
        // PC（ドライブ一覧）と余白は実在するフォルダーではないので、ツリー自体の操作だけ出す
        if (node is not { Path.Length: > 0 } target)
        {
            AddMenuItem(flyout, "Text.Tree.Refresh", () => _ = vm.RefreshSidebarTreeAsync());
            AddMenuItem(flyout, "Text.Tree.CollapseAll", vm.CollapseSidebarTree);
            return;
        }

        var path = target.Path;
        // ドライブ直下を IFileOperation へ渡すと中身の一括操作になるため、変更系は出さない
        var isDrive = IsDriveRoot(path);

        AddMenuItem(flyout, "Text.Common.Open", () => vm.SelectedTab?.NavigateTo(path));
        AddMenuItem(flyout, "Text.Common.OpenInNewTab", () => vm.OpenInNewTab(path));
        AddMenuItem(flyout, LocalizationService.Text("Text.Tab.PinInTabs", 1), () => vm.PinFolderTabs([path]));
        AddMenuItem(flyout, "Text.Common.AddToBookmarks", () => vm.AddBookmark(path));

        flyout.Items.Add(new Separator());
        AddMenuItem(flyout, "Text.Tree.NewFile", () => _ = vm.BeginNewTreeItemAsync(isFile: true, target));
        AddMenuItem(flyout, "Text.Tree.NewFolder", () => _ = vm.BeginNewTreeItemAsync(isFile: false, target));

        flyout.Items.Add(new Separator());
        if (!isDrive)
        {
            AddMenuItem(flyout, "Text.Command.Cut", () => SetTreeClipboard(vm, path, cut: true));
            AddMenuItem(flyout, "Text.Common.Copy", () => SetTreeClipboard(vm, path, cut: false));
        }

        var paste = AddMenuItem(flyout, "Text.Command.Paste", () => _ = PasteIntoTreeFolderAsync(vm, path));
        paste.IsEnabled = ClipboardFileService.HasFiles();
        AddMenuItem(flyout, "Text.Common.CopyPath", () => _ = CopyTreePathAsync(vm, path));

        if (!isDrive)
        {
            flyout.Items.Add(new Separator());
            AddMenuItem(flyout, "Text.Command.Rename", () => _ = RenameTreeFolderAsync(vm, target));
            AddMenuItem(flyout, "Text.Common.Delete", () => _ = DeleteTreeFolderAsync(vm, target));
        }

        flyout.Items.Add(new Separator());
        AddMenuItem(flyout, "Text.Common.OpenInExplorer", () => vm.SelectedTab?.OpenFolderInExplorer(path));
        AddMenuItem(flyout, "Text.Tree.Refresh", () => _ = vm.RefreshSidebarTreeAsync());
        AddMenuItem(flyout, "Text.Tree.CollapseAll", vm.CollapseSidebarTree);

        flyout.Items.Add(new Separator());
        AddMenuItem(flyout, "Text.Common.Properties", () => FileOperationService.ShowProperties(path));
        if (vm.SelectedTab is { IsSettingsTab: false } tab)
        {
            // シェル拡張の項目（圧縮ツール等）はここから。フォルダー一覧と同じ Windows 標準メニューを出す
            AddMenuItem(flyout, "Text.Menu.ShowMoreOptions", () => ShowShellContextMenu(tab, path, screen));
        }
    }

    /// <summary>ローカライズキー（または確定済みの文字列）から項目を 1 つ足す。</summary>
    private static MenuItem AddMenuItem(MenuFlyout flyout, string keyOrText, Action invoke)
    {
        var item = new MenuItem
        {
            Header = keyOrText.StartsWith("Text.", StringComparison.Ordinal)
                ? LocalizationService.Text(keyOrText)
                : keyOrText,
        };
        item.Click += (_, _) => invoke();
        flyout.Items.Add(item);
        return item;
    }

    /// <summary>ドライブ直下（C:\ など）か。変更系の操作を出さない判定に使う。</summary>
    private static bool IsDriveRoot(string path)
        => System.IO.Path.GetPathRoot(path) is { Length: > 0 } root
           && string.Equals(root, path, StringComparison.OrdinalIgnoreCase);

    private static void SetTreeClipboard(MainWindowViewModel vm, string path, bool cut)
    {
        if (!ClipboardFileService.SetFiles([path], cut))
        {
            SetTreeStatus(vm, LocalizationService.Text("Text.Clipboard.WriteFailed"));
            return;
        }

        // 貼り付けの活性状態は各タブが持っているので、まとめて再評価させる
        foreach (var tab in vm.Tabs)
        {
            tab.NotifyClipboardChanged();
        }

        SetTreeStatus(vm, LocalizationService.Text(cut ? "Text.Clipboard.Cut" : "Text.Clipboard.Copied", 1));
    }

    private async Task CopyTreePathAsync(MainWindowViewModel vm, string path)
    {
        if (vm.SelectedTab is { IsSettingsTab: false } tab)
        {
            await CopyPathTextAsync(tab, $"\"{path}\"");
        }
    }

    /// <summary>ツリーのフォルダーへ貼り付ける（一覧の貼り付けと同じ規則：切り取りなら移動、同一フォルダーなら自動リネーム）。</summary>
    private static async Task PasteIntoTreeFolderAsync(MainWindowViewModel vm, string dest)
    {
        var files = ClipboardFileService.GetFiles(out var isCut);
        if (files.Count == 0)
        {
            return;
        }

        var sameDir = !isCut && files.Any(f => WindowsPathIdentity.Instance.Equals(
            System.IO.Path.GetDirectoryName(f), dest));
        var result = await Task.Run(() => FileOperationService.CopyOrMove(files, dest, move: isCut, renameOnCollision: sameDir));
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
            {
                SetTreeStatus(vm, LocalizationService.Text("Text.Op.PasteFailed", FormatTreeOpError(result.NativeErrorCode)));
            }

            return;
        }

        if (isCut)
        {
            ClipboardFileService.Clear();
        }

        await AfterTreeMutationAsync(vm);
    }

    private async Task RenameTreeFolderAsync(MainWindowViewModel vm, FolderTreeNode target)
    {
        var newName = await PromptTextAsync(LocalizationService.Text("Text.Command.Rename"), target.Name);
        if (newName is null)
        {
            return;
        }

        newName = newName.Trim();
        var dir = System.IO.Path.GetDirectoryName(target.Path);
        if (newName.Length == 0 || newName == target.Name || dir is null)
        {
            return;
        }

        if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            SetTreeStatus(vm, LocalizationService.Text("Text.Rename.InvalidChars"));
            return;
        }

        var newPath = System.IO.Path.Combine(dir, newName);
        if (Directory.Exists(newPath) || File.Exists(newPath))
        {
            SetTreeStatus(vm, LocalizationService.Text("Text.Rename.AlreadyExists", newName));
            return;
        }

        var result = FileOperationService.Rename(target.Path, newPath);
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
            {
                SetTreeStatus(vm, LocalizationService.Text("Text.Op.RenameFailed", FormatTreeOpError(result.NativeErrorCode)));
            }

            return;
        }

        await AfterTreeMutationAsync(vm);
    }

    private static async Task DeleteTreeFolderAsync(MainWindowViewModel vm, FolderTreeNode target)
    {
        var recycled = new List<RecycledItem>();
        var result = await Task.Run(() => FileOperationService.DeleteToRecycleBin([target.Path], permanent: false, recycled));
        if (!result.IsSuccess)
        {
            if (!result.IsCancelled)
            {
                SetTreeStatus(vm, LocalizationService.Text("Text.Op.DeleteFailed", FormatTreeOpError(result.NativeErrorCode)));
            }

            return;
        }

        // 一覧の削除と同じく Ctrl+Z で戻せるようにする
        FileUndoService.PushDelete(recycled);
        await AfterTreeMutationAsync(vm);
    }

    /// <summary>
    /// ツリー経由でフォルダー構成を変えた後の反映。ツリーはファイル監視を持たないので明示的に再列挙し、
    /// 表示中のタブは（監視が効かない場合の保険として）フォルダー一覧を読み直す。
    /// </summary>
    private static async Task AfterTreeMutationAsync(MainWindowViewModel vm)
    {
        await vm.RefreshSidebarTreeAsync();
        if (vm.SelectedTab is { IsSettingsTab: false } tab)
        {
            RefreshAfterShellOperation(tab);
        }
    }

    private static void SetTreeStatus(MainWindowViewModel vm, string text)
    {
        if (vm.SelectedTab is { IsSettingsTab: false } tab)
        {
            tab.StatusText = text;
        }
    }

    /// <summary>ファイル操作エラーを「エラー 206: パスが長すぎます」の形式に整形する。</summary>
    private static string FormatTreeOpError(int code)
    {
        var desc = FileOperationService.DescribeError(code);
        return desc.Length > 0
            ? LocalizationService.Text("Text.Error.CodeWithDesc", code, desc)
            : LocalizationService.Text("Text.Error.Code", code);
    }
}
