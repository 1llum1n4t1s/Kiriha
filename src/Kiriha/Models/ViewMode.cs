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

    /// <summary>
    /// ギャラリー表示。1 枚を大きく出し、下部のフィルムストリップで前後へ送る。
    /// 以前はアイコン表示のサイズスライダーを最大まで上げた状態を指す派生状態だったが、
    /// ステータスバー / 表示メニューから明示的に選ぶモードへ変更した（2026-08-06）。
    /// 既存の保存値（settings.json / folder-views.json）との互換のため、末尾に足すこと。
    /// </summary>
    Gallery,
}
