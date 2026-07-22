using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kiriha.Services;

namespace Kiriha.Models;

/// <summary>サイドバーのツリービュー 1 ノード。Windows XP 時代の
/// 「デスクトップ > マイ ドキュメント / マイ コンピュータ（ドライブ）」構成で、
/// フォルダーのみを展開時に遅延列挙する。</summary>
public sealed partial class FolderTreeNode : ObservableObject
{
    /// <summary>ノードの種別（子の列挙方法が変わる）。</summary>
    public enum NodeKind
    {
        /// <summary>通常フォルダー（サブフォルダーを列挙）。</summary>
        Folder,

        /// <summary>ルートのデスクトップ（マイ ドキュメント + マイ コンピュータ + デスクトップ配下）。</summary>
        Desktop,

        /// <summary>マイ コンピュータ（ドライブを列挙）。</summary>
        Computer,
    }

    public required string Name { get; init; }

    /// <summary>ナビゲーション先パス。マイ コンピュータは FileSystemService.ComputerPath（空文字）。</summary>
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
        => Children.Add(new FolderTreeNode { Name = "読み込み中...", Path = "", Icon = "" });

    /// <summary>子の列挙を開始し、完了を待てるようにする（初回のみ実列挙、以降は同じ Task を返す）。
    /// UI スレッドから呼ぶこと。</summary>
    public Task EnsureChildrenAsync()
        => _loadTask ??= LoadChildrenAsync();

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
            NodeKind.Desktop => BuildDesktopChildren(path),
            NodeKind.Computer => BuildDriveChildren(),
            _ => BuildFolderChildren(path),
        };

    /// <summary>XP のツリー構成: マイ ドキュメント → マイ コンピュータ → デスクトップ上のフォルダー。</summary>
    private static List<FolderTreeNode> BuildDesktopChildren(string desktopPath)
    {
        var children = new List<FolderTreeNode>();
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0)
        {
            children.Add(CreateFolderNode(documents, "マイ ドキュメント", "📄"));
        }

        var computer = new FolderTreeNode
        {
            Name = "マイ コンピュータ",
            Path = FileSystemService.ComputerPath,
            Icon = "💻",
            Kind = NodeKind.Computer,
        };
        computer.AddPlaceholder();
        children.Add(computer);

        children.AddRange(EnumerateSubfolderNodes(desktopPath));
        return children;
    }

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
