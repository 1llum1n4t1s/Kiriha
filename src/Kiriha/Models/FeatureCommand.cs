namespace Kiriha.Models;

/// <summary>コマンドパレットに表示する、検索可能な機能項目。</summary>
public sealed record FeatureCommand(string Title, string Category, string Kind, string Value)
{
    public string SearchText => $"{Title} {Category} {Value}";
}
