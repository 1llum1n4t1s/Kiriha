namespace Kiriha.Services;

/// <summary>フォルダー内で 1 項目に起きた変化の種類。</summary>
public enum DirectoryChangeKind
{
    /// <summary>項目が増えた（作成・移動で入ってきた・名前変更後の新しい名前）。</summary>
    Created,

    /// <summary>項目が消えた（削除・移動で出て行った・名前変更前の古い名前）。</summary>
    Deleted,

    /// <summary>項目はそのままで中身や属性が変わった。</summary>
    Updated,
}

/// <summary>フォルダー内の 1 項目に起きた変化。</summary>
/// <param name="Kind">変化の種類。</param>
/// <param name="FullPath">対象のフルパス。</param>
public sealed record DirectoryChange(DirectoryChangeKind Kind, string FullPath);

/// <summary>短い時間内に届いた変更をまとめた通知。</summary>
public sealed class DirectoryChangeBatch
{
    /// <summary>この束に含まれる最後のファイルシステムイベントの UTC 時刻。</summary>
    public required DateTime LastEventUtc { get; init; }

    /// <summary>
    /// 差分では追随できず、フォルダー全体を読み直す必要があるか
    /// （監視のバッファ溢れ・エラーからの復帰・1 度に大量の変更が来た場合）。
    /// </summary>
    public required bool NeedsFullReload { get; init; }

    /// <summary>個別の変更（同じパスに対する複数のイベントは 1 件へまとめてある）。</summary>
    public required IReadOnlyList<DirectoryChange> Changes { get; init; }
}
