namespace Kiriha.Services;

/// <summary>
/// Ctrl+Z（元に戻す）用の操作履歴。エクスプローラーと同じくウィンドウ単位ではなくアプリ全体で 1 本持つ
/// （どのタブで削除しても、その直後の Ctrl+Z で戻せる）。
/// <para>
/// 現在の対象は「ごみ箱への削除」だけ。移動やコピーは IFileOperation 側に FOF_ALLOWUNDO を渡してあり、
/// エクスプローラーの取り消し履歴には載るが、こちらの履歴には積んでいない。
/// </para>
/// </summary>
internal static class FileUndoService
{
    /// <summary>積みっぱなしでメモリを食わないよう上限を設ける（エクスプローラーも履歴は有限）。</summary>
    private const int Capacity = 32;

    private static readonly List<IReadOnlyList<RecycledItem>> Deletions = [];
    private static readonly Lock Gate = new();

    /// <summary>戻せる操作があるか。</summary>
    public static bool CanUndo
    {
        get
        {
            lock (Gate)
            {
                return Deletions.Count > 0;
            }
        }
    }

    /// <summary>削除を履歴へ積む（ごみ箱へ入った項目が 1 件も無ければ何もしない）。</summary>
    public static void PushDelete(IReadOnlyList<RecycledItem> recycled)
    {
        if (recycled.Count == 0)
        {
            return;
        }

        lock (Gate)
        {
            Deletions.Add(recycled);
            if (Deletions.Count > Capacity)
            {
                Deletions.RemoveAt(0);
            }
        }
    }

    /// <summary>直近の操作を取り出す（無ければ null）。</summary>
    public static IReadOnlyList<RecycledItem>? PopDelete()
    {
        lock (Gate)
        {
            if (Deletions.Count == 0)
            {
                return null;
            }

            var last = Deletions[^1];
            Deletions.RemoveAt(Deletions.Count - 1);
            return last;
        }
    }
}
