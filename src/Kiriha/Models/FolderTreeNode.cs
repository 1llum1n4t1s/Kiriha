using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kiriha.Services;

namespace Kiriha.Models;

/// <summary>サイドバーのツリービュー 1 ノード。ルートの PC からドライブ、その配下の実フォルダーへと
/// 物理パスのとおりに降りる構成（PC > C: > Users > IMT > Desktop）で、フォルダーのみを展開時に遅延列挙する。
/// 以前は XP 風に「デスクトップ > マイ ドキュメント / マイ コンピュータ」という仮想ルートを挟んでいたが、
/// ツリーの階層とアドレスバーのパスが一致しないため 2026-08-05 に実パス構成へ変更した。
/// （sealed でないのは、インライン新規作成の入力行 <see cref="NewTreeItemNode"/> が継承するため）</summary>
public partial class FolderTreeNode : ObservableObject
{
    /// <summary>ノードの種別（子の列挙方法が変わる）。</summary>
    public enum NodeKind
    {
        /// <summary>通常フォルダー（サブフォルダーを列挙）。</summary>
        Folder,

        /// <summary>ルートの PC（ドライブを列挙）。</summary>
        Computer,
    }

    public required string Name { get; init; }

    /// <summary>ナビゲーション先パス。PC は FileSystemService.ComputerPath（空文字）。</summary>
    public required string Path { get; init; }

    public required string Icon { get; init; }

    public NodeKind Kind { get; init; } = NodeKind.Folder;

    private Task? _loadTask;

    public ObservableCollection<FolderTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = EnsureChildrenAsync();
        }
    }

    /// <summary>展開矢印を出すためのプレースホルダー。実際の子は展開時に置き換える。</summary>
    public void AddPlaceholder()
        => Children.Add(new FolderTreeNode { Name = LocalizationService.Text("Text.Tree.Loading"), Path = "", Icon = "" });

    /// <summary>子の列挙を開始し、完了を待てるようにする（初回のみ実列挙、以降は同じ Task を返す）。
    /// UI スレッドから呼ぶこと。</summary>
    public Task EnsureChildrenAsync()
        => _loadTask ??= LoadChildrenAsync();

    /// <summary>子を一度でも列挙し始めたか（「最新の情報に更新」で再列挙すべきノードの判定に使う）。</summary>
    public bool HasLoadedChildren => _loadTask is not null;

    /// <summary>子フォルダーを列挙し直す（「最新の情報に更新」用。キャッシュ済みでもやり直す）。
    /// UI スレッドから呼ぶこと。</summary>
    public Task ReloadChildrenAsync()
    {
        _loadTask = LoadChildrenAsync();
        return _loadTask;
    }

    private async Task LoadChildrenAsync()
    {
        var kind = Kind;
        var path = Path;
        List<FolderTreeNode> children;
        try
        {
            children = await Task.Run(() => BuildChildren(kind, path));
        }
        catch (Exception ex)
        {
            Logger.LogException($"ツリーの子フォルダーを列挙できませんでした: {path}", ex);
            children = [];
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Children.Clear();
            foreach (var child in children)
            {
                Children.Add(child);
            }
        });
    }

    private static List<FolderTreeNode> BuildChildren(NodeKind kind, string path)
        => kind switch
        {
            NodeKind.Computer => BuildDriveChildren(),
            _ => BuildFolderChildren(path),
        };

    private static List<FolderTreeNode> BuildDriveChildren()
    {
        var children = new List<FolderTreeNode>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            children.Add(CreateFolderNode(
                drive.RootDirectory.FullName,
                FileSystemService.GetDriveLabel(drive),
                "💾"));
        }

        return children;
    }

    private static List<FolderTreeNode> BuildFolderChildren(string path)
        => EnumerateSubfolderNodes(path);

    private static List<FolderTreeNode> EnumerateSubfolderNodes(string path)
    {
        var children = new List<FolderTreeNode>();
        if (path.Length == 0 || !Directory.Exists(path))
        {
            return children;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var dir in new DirectoryInfo(path).EnumerateDirectories("*", options)
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            children.Add(CreateFolderNode(dir.FullName, dir.Name, "📁"));
        }

        return children;
    }

    private static FolderTreeNode CreateFolderNode(string path, string name, string icon)
    {
        var node = new FolderTreeNode { Name = name, Path = path, Icon = icon };
        node.AddPlaceholder();
        return node;
    }
}
