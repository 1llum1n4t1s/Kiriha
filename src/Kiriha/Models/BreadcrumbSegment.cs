namespace Kiriha.Models;

/// <summary>アドレスバーのパンくずリスト 1 要素。</summary>
public sealed class BreadcrumbSegment
{
    public required string Name { get; init; }

    public required string Path { get; init; }
}
