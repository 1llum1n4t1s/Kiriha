namespace Kiriha.Models;

/// <summary>お気に入りバーの 1 要素（Children が非 null ならフォルダー）。settings.json に永続化する。</summary>
public sealed class BookmarkNode
{
    public string Name { get; set; } = "";

    /// <summary>リンク先パス。フォルダーノードでは null。</summary>
    public string? Path { get; set; }

    /// <summary>子ノード（フォルダーの場合のみ）。</summary>
    public List<BookmarkNode>? Children { get; set; }

    public bool IsFolder => Children is not null;
}
