namespace Kiriha.Models;

/// <summary>ギャラリー表示のメタ情報パネルに出す 1 行（ラベルと表示値）。</summary>
public sealed record GalleryMetadataItem(string Label, string Value);
