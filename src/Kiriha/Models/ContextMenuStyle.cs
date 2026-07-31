namespace Kiriha.Models;

/// <summary>
/// ファイル一覧を右クリックしたときに出すコンテキストメニューの実装方式。
/// 値は settings.json へ enum 名の文字列で保存する（<see cref="Services.AppSettings.ContextMenuStyle"/>）。
/// </summary>
public enum ContextMenuStyle
{
    /// <summary>
    /// Kiriha 独自描画。Windows 11 のメニューに合わせ、上部のアイコン行と厳選した項目を自前で並べ、
    /// シェル拡張の項目だけを取り込む。末尾の「その他のオプションを確認」で <see cref="System"/> へ逃がす。
    /// </summary>
    Modern,

    /// <summary>
    /// Kiriha 独自描画。IContextMenu が返す項目をそのまま並べる（内容はクラシックメニュー相当、
    /// 見た目だけ Windows 11 風）。シェル拡張の取りこぼしが無いのが利点。
    /// </summary>
    Shell,

    /// <summary>Windows 標準の Win32 メニュー（TrackPopupMenuEx）をそのまま表示する。</summary>
    System,
}
