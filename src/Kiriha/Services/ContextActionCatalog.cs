using Kiriha.Models;

namespace Kiriha.Services;

/// <summary>タブ・項目・背景・条件選択に追加した100件の実機能カタログ。</summary>
public static class ContextActionCatalog
{
    public static IReadOnlyList<ContextAction> All { get; } = Build();

    public static IEnumerable<ContextAction> For(ActionScope scope) => All.Where(x => x.Scope == scope);

    private static List<ContextAction> Build()
    {
        var items = new List<ContextAction>(100);
        Add(items, ActionScope.Tab, "タブ", new[]
        {
            ("tab.close-left", "左側のタブを閉じる"), ("tab.close-duplicates", "同じ場所の重複タブを閉じる"),
            ("tab.close-unpinned", "固定されていないタブをすべて閉じる"), ("tab.pin-all", "すべてのタブを固定"),
            ("tab.unpin-all", "すべてのタブの固定を解除"), ("tab.pin-left", "左側のタブを固定"),
            ("tab.pin-right", "右側のタブを固定"), ("tab.reload-all", "すべてのタブを再読み込み"),
            ("tab.reload-left", "左側のタブを再読み込み"), ("tab.reload-right", "右側のタブを再読み込み"),
            ("tab.move-first", "タブを先頭へ移動"), ("tab.move-last", "タブを末尾へ移動"),
            ("tab.sort-title", "タブを名前順に並べ替え"), ("tab.sort-path", "タブをパス順に並べ替え"),
            ("tab.reverse", "タブの並び順を反転"), ("tab.open-parent", "親フォルダーを新しいタブで開く"),
            ("tab.copy-title", "タブ名をコピー"), ("tab.copy-uri", "タブを file URI としてコピー"),
            ("tab.copy-markdown", "タブを Markdown リンクとしてコピー"), ("tab.copy-all-paths", "すべてのタブのパスをコピー"),
        });
        Add(items, ActionScope.File, "項目", new[]
        {
            ("file.copy-path", "フルパスをコピー"), ("file.copy-quoted", "引用符付きパスをコピー"),
            ("file.copy-name", "名前をコピー"), ("file.copy-stem", "拡張子なしの名前をコピー"),
            ("file.copy-extension", "拡張子をコピー"), ("file.copy-parent", "親フォルダーのパスをコピー"),
            ("file.copy-uri", "file URI をコピー"), ("file.copy-markdown", "Markdown リンクをコピー"),
            ("file.copy-html", "HTML リンクをコピー"), ("file.copy-json", "JSON 文字列としてコピー"),
            ("file.copy-json-array", "選択項目を JSON 配列としてコピー"), ("file.copy-powershell", "PowerShell リテラルとしてコピー"),
            ("file.copy-cmd", "コマンドプロンプト用にコピー"), ("file.copy-slashes", "スラッシュ区切りでコピー"),
            ("file.copy-wsl", "WSL パスとしてコピー"), ("file.copy-size", "サイズをコピー"),
            ("file.copy-modified", "更新日時を ISO 形式でコピー"), ("file.copy-created", "作成日時を ISO 形式でコピー"),
            ("file.copy-attributes", "属性をコピー"), ("file.copy-summary", "項目情報の要約をコピー"),
            ("file.hash-sha256", "SHA-256 を計算してコピー"), ("file.hash-sha1", "SHA-1 を計算してコピー"),
            ("file.hash-md5", "MD5 を計算してコピー"), ("file.copy-tsv", "項目情報を TSV でコピー"),
            ("file.copy-getitem", "PowerShell Get-Item コマンドをコピー"), ("file.open-parent", "親フォルダーへ移動"),
            ("file.open-parent-tab", "親フォルダーを新しいタブで開く"), ("file.explorer-select", "エクスプローラーで選択"),
            ("file.open-notepad", "メモ帳で開く"), ("file.terminal-here", "この場所でターミナルを開く"),
            ("file.powershell-here", "この場所で PowerShell を開く"), ("file.cmd-here", "この場所でコマンドプロンプトを開く"),
            ("file.vscode", "Visual Studio Code で開く"), ("file.duplicate", "複製を作成"),
            ("file.backup", "タイムスタンプ付きバックアップを作成"), ("file.rename-lower", "名前を小文字に変換"),
            ("file.rename-upper", "名前を大文字に変換"), ("file.rename-underscores", "名前の空白をアンダースコアに変換"),
            ("file.touch", "更新日時を現在時刻にする"), ("file.copy-relative", "現在のフォルダーからの相対パスをコピー"),
        });
        Add(items, ActionScope.Background, "背景", new[]
        {
            ("bg.create-txt", "空のテキストファイルを作成"), ("bg.create-md", "Markdown ファイルを作成"),
            ("bg.create-json", "JSON ファイルを作成"), ("bg.create-yaml", "YAML ファイルを作成"),
            ("bg.create-csv", "CSV ファイルを作成"), ("bg.create-html", "HTML ファイルを作成"),
            ("bg.create-css", "CSS ファイルを作成"), ("bg.create-js", "JavaScript ファイルを作成"),
            ("bg.create-ps1", "PowerShell スクリプトを作成"), ("bg.create-gitignore", ".gitignore を作成"),
            ("bg.copy-path", "現在のパスをコピー"), ("bg.copy-quoted", "現在のパスを引用符付きでコピー"),
            ("bg.copy-uri", "現在の場所を file URI としてコピー"), ("bg.copy-ps-cd", "PowerShell の移動コマンドをコピー"),
            ("bg.copy-cmd-cd", "コマンドプロンプトの移動コマンドをコピー"), ("bg.open-terminal", "Windows Terminal をここで開く"),
            ("bg.open-powershell", "PowerShell をここで開く"), ("bg.open-cmd", "コマンドプロンプトをここで開く"),
            ("bg.open-vscode", "このフォルダーを Visual Studio Code で開く"), ("bg.open-notepad", "メモ帳をここで開く"),
        });
        Add(items, ActionScope.Selection, "条件選択", new[]
        {
            ("select.images", "画像を選択"), ("select.videos", "動画を選択"), ("select.audio", "音声を選択"),
            ("select.documents", "文書を選択"), ("select.archives", "アーカイブを選択"), ("select.code", "ソースコードを選択"),
            ("select.hidden", "隠し項目を選択"), ("select.visible", "隠し項目以外を選択"),
            ("select.empty", "空のファイルを選択"), ("select.nonempty", "空でないファイルを選択"),
            ("select.today", "今日更新された項目を選択"), ("select.week", "7日以内に更新された項目を選択"),
            ("select.old", "30日より前の項目を選択"), ("select.dotfiles", "ドットで始まる項目を選択"),
            ("select.spaces", "名前に空白を含む項目を選択"), ("select.digits", "名前に数字を含む項目を選択"),
            ("select.executable", "実行可能ファイルを選択"), ("select.readonly", "読み取り専用項目を選択"),
            ("select.largest", "サイズが大きい10項目を選択"), ("select.smallest", "サイズが小さい10項目を選択"),
        });
        if (items.Count != 100 || items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != 100)
            throw new InvalidOperationException("追加機能カタログは重複なしの100件である必要があります");
        return items;
    }

    private static void Add(List<ContextAction> target, ActionScope scope, string category,
        IEnumerable<(string Id, string Title)> items)
        => target.AddRange(items.Select(x => new ContextAction(x.Id, x.Title, category, scope)));
}
