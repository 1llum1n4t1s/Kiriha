using System.Runtime.CompilerServices;

// Services / Models の多くが internal（シェル interop の実装詳細を外に出さない方針）のため、
// テストプロジェクトにだけ内部可視性を与える。
[assembly: InternalsVisibleTo("Kiriha.Tests")]
