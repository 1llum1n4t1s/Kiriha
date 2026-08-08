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
/// 中身の組み立ては統一ビルダー（MainWindow.UnifiedMenu.cs）に任せ、ここは
/// 「どのノードが対象か」を決めてツリー固有の項目を渡すだけにしてある。
/// ファイル操作はいずれも <see cref="ContextMenuSource"/> ごとの分岐を持たず、
/// パス指定版（MainWindow.PathOperations.cs）を通る。
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

        e.Handled = true;
        var screen = this.PointToScreen(e.GetPosition(this));

        // PC（ドライブ一覧）と余白は実在するフォルダーではないので、ツリー自体の操作だけ出す
        var target = node is { Path.Length: > 0 } folder
            ? new ContextMenuTarget(ContextMenuSource.Tree, [folder.Path], vm, screen)
            { AllDirectories = true, TreeNode = folder }
            : null;

        ShowUnifiedContextMenu(tree, target, BuildTreeOwnEntries(vm, node), screen);
    }

    /// <summary>ツリー固有の項目（新規作成・再読み込み・折りたたみ）。</summary>
    private List<ExplorerMenuEntry> BuildTreeOwnEntries(MainWindowViewModel vm, FolderTreeNode? node)
    {
        var entries = new List<ExplorerMenuEntry>();
        if (node is { Path.Length: > 0 } target)
        {
            // ツリー固有の項目にはグリフを付けない。Segoe に「新規ファイル」「すべて折りたたむ」に
            // ちょうど合う字体が無く、近い字を当てると Windows 10 の Segoe MDL2 で豆腐になる恐れがある
            // （ExplorerContextMenu は Glyph が null なら余白だけ空けて描く）。
            entries.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Tree.NewFile"),
                Invoke = () => _ = vm.BeginNewTreeItemAsync(isFile: true, target),
            });
            entries.Add(new ExplorerMenuEntry
            {
                Text = LocalizationService.Text("Text.Tree.NewFolder"),
                Glyph = GlyphNewFolder,
                Invoke = () => _ = vm.BeginNewTreeItemAsync(isFile: false, target),
            });
            entries.Add(ExplorerMenuEntry.Separator);
        }

        entries.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Tree.Refresh"),
            Glyph = GlyphRefresh,
            Invoke = () => _ = vm.RefreshSidebarTreeAsync(),
        });
        entries.Add(new ExplorerMenuEntry
        {
            Text = LocalizationService.Text("Text.Tree.CollapseAll"),
            Invoke = vm.CollapseSidebarTree,
        });
        return entries;
    }
}
