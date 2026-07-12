namespace Kiriha.Models;

/// <summary>左ペインのセクション見出し（クイックアクセス / PC）。</summary>
public sealed class SidebarHeader
{
    public required string Title { get; init; }
}

/// <summary>左ペインのナビゲーション項目。</summary>
public sealed class SidebarLink
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string Icon { get; init; }

    /// <summary>クイックアクセス項目かどうか（ピン留め解除メニューの表示可否）。</summary>
    public bool IsQuickAccess { get; init; }

    /// <summary>ホバー時のツールチップ（パスやドライブ空き容量）。</summary>
    public string? Tooltip { get; init; }

    /// <summary>shell: コマンド項目（ごみ箱など。エクスプローラーに委譲して開く）。</summary>
    public bool IsShellCommand { get; init; }

    /// <summary>ファイル項目（最近使用したファイル。関連付けで起動する）。</summary>
    public bool IsFile { get; init; }
}
