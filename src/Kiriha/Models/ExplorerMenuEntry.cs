using Avalonia.Media;

namespace Kiriha.Models;

/// <summary>
/// 独自描画コンテキストメニューの 1 項目。シェル由来の項目（<see cref="Image"/> 付き）と
/// Kiriha 自前の項目（<see cref="Glyph"/> 付き）を同じ形で扱うための表示用モデル。
/// </summary>
/// <remarks>
/// 実行は <see cref="Invoke"/> をそのまま呼ぶのではなく、メニューを閉じてから
/// <see cref="Controls.ExplorerContextMenu"/> がまとめて実行する。シェル拡張の verb は
/// ポップアップが出たままだとモーダル UI を正しく出せないことがあるため。
/// </remarks>
public sealed class ExplorerMenuEntry
{
    /// <summary>表示名。ニーモニックの &amp; は取り除いた状態で入れる。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>右端に薄く出すショートカットキー（"Ctrl+C" 等）。無ければ null。</summary>
    public string? Shortcut { get; init; }

    /// <summary>Segoe Fluent Icons のグリフ（Kiriha 自前の項目で使う）。</summary>
    public string? Glyph { get; init; }

    /// <summary>シェルから取り出したアイコン画像（シェル項目で使う）。</summary>
    public IImage? Image { get; init; }

    /// <summary>区切り線なら true（他のフィールドは無視される）。</summary>
    public bool IsSeparator { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>既定の動作（エクスプローラーで太字になる項目）。</summary>
    public bool IsDefault { get; init; }

    public bool IsChecked { get; init; }

    /// <summary>サブメニュー。空ならただの項目。</summary>
    public IReadOnlyList<ExplorerMenuEntry> Children { get; init; } = [];

    /// <summary>選択されたときに実行する処理。サブメニュー親や区切り線では null。</summary>
    public Action? Invoke { get; init; }

    /// <summary>区切り線を 1 本作る。</summary>
    public static ExplorerMenuEntry Separator { get; } = new() { IsSeparator = true };
}
