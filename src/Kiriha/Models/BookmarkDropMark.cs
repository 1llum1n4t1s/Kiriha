namespace Kiriha.Models;

/// <summary>お気に入りへドラッグ中に、どこへ挿入されるかを表す目印。</summary>
public enum BookmarkDropMark
{
    /// <summary>この項目は対象外。</summary>
    None,

    /// <summary>この項目の直前に挿入する（上端に線を出す）。</summary>
    Before,

    /// <summary>この項目の直後に挿入する（下端に線を出す）。</summary>
    After,

    /// <summary>このフォルダーの中へ入れる（行全体を強調する）。</summary>
    Into,
}
