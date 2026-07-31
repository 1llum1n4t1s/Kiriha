namespace Kiriha.Models;

/// <summary>エクスプローラーの「表示」メニューに対応する表示モード。</summary>
public enum ViewMode
{
    ExtraLargeIcons,
    LargeIcons,
    MediumIcons,
    SmallIcons,
    List,
    Details,

    /// <summary>
    /// エクスプローラーの「並べて表示」。48px アイコンの右へ 名前 / 種類 / サイズ を最大 3 行で並べる。
    /// 既存の保存値（settings.json / folder-views.json）との互換のため、末尾に足すこと。
    /// </summary>
    Tiles,
}
