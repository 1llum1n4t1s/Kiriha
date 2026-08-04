using CommunityToolkit.Mvvm.ComponentModel;

namespace Kiriha.Models;

/// <summary>
/// サイドバーツリーの「新しいファイル / 新しいフォルダー」インライン入力行（VSCode のエクスプローラーと同じ方式）。
/// 作成先フォルダーの先頭子として一時的に挿入され、確定またはキャンセルで取り除かれる。
/// Path は空のままにする（プレースホルダーと同じ扱いになり、選択・ドラッグ・ナビゲーションの対象外になる）。
/// </summary>
public sealed partial class NewTreeItemNode : FolderTreeNode
{
    /// <summary>ファイル作成用なら true、フォルダー作成用なら false。</summary>
    public required bool IsFile { get; init; }

    /// <summary>入力中の名前。VSCode と同じく「a/b/c」のような区切り付き入力も受け付ける。</summary>
    [ObservableProperty]
    private string _editText = "";

    /// <summary>入力欄の下に出す検証エラー（null なら非表示）。</summary>
    [ObservableProperty]
    private string? _validationError;
}
