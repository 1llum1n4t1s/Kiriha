namespace Kiriha.Models;

/// <summary>左ペインの表示内容。3 種を排他で切り替える（チップ 1 個ずつが 1 モードに対応）。
/// 旧バージョンの bool <c>AppSettings.SidebarShowTree</c> はここへ移行する。</summary>
public enum SidebarMode
{
    /// <summary>Windows のクイックアクセス + ドライブ一覧（既定）。</summary>
    QuickAccess,

    /// <summary>物理パスをそのまま辿るフォルダーツリー。</summary>
    Tree,

    /// <summary>お気に入り（旧お気に入りバーの内容をそのまま表示する）。</summary>
    Bookmarks,
}

public static class SidebarModes
{
    /// <summary>保存値から表示モードを決める。未設定・壊れた値のときだけ旧 bool を見る
    /// （settings.json に SidebarMode を持たない既存インストールからの移行経路）。</summary>
    public static SidebarMode Resolve(string? saved, bool legacyShowTree)
    {
        // Enum.TryParse は "5" のような数値文字列も成功扱いにするため IsDefined で未定義値を弾く
        if (Enum.TryParse<SidebarMode>(saved, out var mode) && Enum.IsDefined(mode))
        {
            return mode;
        }

        return legacyShowTree ? SidebarMode.Tree : SidebarMode.QuickAccess;
    }
}
