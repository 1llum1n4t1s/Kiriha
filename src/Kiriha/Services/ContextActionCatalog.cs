using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>タブ操作に追加した20件の実機能カタログ。</summary>
public static class ContextActionCatalog
{
    public static IReadOnlyList<ContextAction> All { get; } = Build();

    public static IEnumerable<ContextAction> For(ActionScope scope) => All.Where(x => x.Scope == scope);

    private static List<ContextAction> Build()
    {
        var items = new List<ContextAction>(20);
        Add(items, ActionScope.Tab, "タブ", new[]
        {
            ("tab.close-left", "上側のタブを閉じる"), ("tab.close-duplicates", "同じ場所の重複タブを閉じる"),
            ("tab.close-unpinned", "固定されていないタブをすべて閉じる"), ("tab.pin-all", "すべてのタブを固定"),
            ("tab.unpin-all", "すべてのタブの固定を解除"), ("tab.pin-left", "上側のタブを固定"),
            ("tab.pin-right", "下側のタブを固定"), ("tab.reload-all", "すべてのタブを再読み込み"),
            ("tab.reload-left", "上側のタブを再読み込み"), ("tab.reload-right", "下側のタブを再読み込み"),
            ("tab.move-first", "タブを先頭へ移動"), ("tab.move-last", "タブを末尾へ移動"),
            ("tab.sort-title", "タブを名前順に並べ替え"), ("tab.sort-path", "タブをパス順に並べ替え"),
            ("tab.reverse", "タブの並び順を反転"), ("tab.open-parent", "親フォルダーを新しいタブで開く"),
            ("tab.copy-title", "タブ名をコピー"), ("tab.copy-uri", "タブを file URI としてコピー"),
            ("tab.copy-markdown", "タブを Markdown リンクとしてコピー"), ("tab.copy-all-paths", "すべてのタブのパスをコピー"),
        });
        if (items.Count != 20 || items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != 20)
            throw new InvalidOperationException("追加機能カタログは重複なしの20件である必要があります");
        return items;
    }

    private static void Add(List<ContextAction> target, ActionScope scope, string category,
        IEnumerable<(string Id, string Title)> items)
        => target.AddRange(items.Select(x => new ContextAction(x.Id, x.Title, category, scope)));
}
