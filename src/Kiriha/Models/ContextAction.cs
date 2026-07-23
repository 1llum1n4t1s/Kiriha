namespace Kiriha.Models;

public enum ActionScope { Tab }

/// <summary>右クリック等から実行できる、独立したアプリ機能の定義。</summary>
public sealed record ContextAction(string Id, string Title, string Category, ActionScope Scope);
