namespace Kiriha.Models;

public enum ActionScope { Tab, File, Background, Selection }

/// <summary>右クリック等から実行できる、独立したアプリ機能の定義。</summary>
public sealed record ContextAction(string Id, string Title, string Category, ActionScope Scope)
{
    /// <summary>表示位置に依存しないサブメニュー分類。</summary>
    public string Group => Scope switch
    {
        ActionScope.File when Id is "file.copy-path" or "file.copy-quoted" or "file.copy-name"
            or "file.copy-stem" or "file.copy-extension" or "file.copy-parent" or "file.copy-uri"
            or "file.copy-markdown" or "file.copy-html" or "file.copy-json" or "file.copy-json-array"
            or "file.copy-powershell" or "file.copy-cmd" or "file.copy-slashes" or "file.copy-wsl" => "path",
        ActionScope.File when Id is "file.copy-size" or "file.copy-modified" or "file.copy-created"
            or "file.copy-attributes" or "file.copy-summary" or "file.hash-sha256" or "file.hash-sha1"
            or "file.hash-md5" or "file.copy-tsv" or "file.copy-getitem" => "info",
        ActionScope.File when Id is "file.open-parent" or "file.open-parent-tab" or "file.explorer-select"
            or "file.open-notepad" or "file.terminal-here" or "file.powershell-here" or "file.cmd-here"
            or "file.vscode" => "open",
        ActionScope.File => "organize",
        ActionScope.Background when Id.StartsWith("bg.create-", StringComparison.Ordinal) => "create",
        ActionScope.Background when Id.StartsWith("bg.copy-", StringComparison.Ordinal) => "copy",
        ActionScope.Background => "open",
        ActionScope.Selection => "selection",
        _ => "default",
    };
}
