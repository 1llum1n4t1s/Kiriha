using Kiriha.Services;

namespace Kiriha.Models;

public enum ActionScope { Tab }

/// <summary>右クリック等から実行できる、独立したアプリ機能の定義。
/// カタログは起動時に 1 回だけ構築されるため、表示文字列はロケールキーで保持し、
/// <see cref="Title"/> / <see cref="Category"/> を読むたびに現在の言語で解決する。</summary>
public sealed record ContextAction(string Id, string TitleKey, string CategoryKey, ActionScope Scope)
{
    /// <summary>メニューに出す表示名（現在の言語）。</summary>
    public string Title => LocalizationService.Text(TitleKey);

    /// <summary>グループ見出し（現在の言語）。</summary>
    public string Category => LocalizationService.Text(CategoryKey);
}
