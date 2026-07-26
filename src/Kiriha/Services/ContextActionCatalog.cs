using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>タブ操作に追加した20件の実機能カタログ。
/// 保持するのはロケールキーで、表示名は ContextAction.Title が都度解決する。</summary>
public static class ContextActionCatalog
{
    public static IReadOnlyList<ContextAction> All { get; } = Build();

    public static IEnumerable<ContextAction> For(ActionScope scope) => All.Where(x => x.Scope == scope);

    private static List<ContextAction> Build()
    {
        var items = new List<ContextAction>(20);
        Add(items, ActionScope.Tab, "Text.Tab.Category", new[]
        {
            ("tab.close-left", "Text.TabAction.CloseLeft"), ("tab.close-duplicates", "Text.TabAction.CloseDuplicates"),
            ("tab.close-unpinned", "Text.TabAction.CloseUnpinned"), ("tab.pin-all", "Text.TabAction.PinAll"),
            ("tab.unpin-all", "Text.TabAction.UnpinAll"), ("tab.pin-left", "Text.TabAction.PinLeft"),
            ("tab.pin-right", "Text.TabAction.PinRight"), ("tab.reload-all", "Text.TabAction.ReloadAll"),
            ("tab.reload-left", "Text.TabAction.ReloadLeft"), ("tab.reload-right", "Text.TabAction.ReloadRight"),
            ("tab.move-first", "Text.TabAction.MoveFirst"), ("tab.move-last", "Text.TabAction.MoveLast"),
            ("tab.sort-title", "Text.TabAction.SortByName"), ("tab.sort-path", "Text.TabAction.SortByPath"),
            ("tab.reverse", "Text.TabAction.Reverse"), ("tab.open-parent", "Text.TabAction.OpenParent"),
            ("tab.copy-title", "Text.TabAction.CopyTitle"), ("tab.copy-uri", "Text.TabAction.CopyUri"),
            ("tab.copy-markdown", "Text.TabAction.CopyMarkdown"), ("tab.copy-all-paths", "Text.TabAction.CopyAllPaths"),
        });
        if (items.Count != 20 || items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != 20)
            throw new InvalidOperationException("追加機能カタログは重複なしの20件である必要があります");
        return items;
    }

    private static void Add(List<ContextAction> target, ActionScope scope, string categoryKey,
        IEnumerable<(string Id, string TitleKey)> items)
        => target.AddRange(items.Select(x => new ContextAction(x.Id, x.TitleKey, categoryKey, scope)));
}
