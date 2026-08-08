namespace Kiriha.Services;

/// <summary>Windowsパスを大小文字と末尾区切りを無視して比較する。</summary>
internal sealed class WindowsPathIdentity : IEqualityComparer<string>
{
    public static WindowsPathIdentity Instance { get; } = new();

    public bool Equals(string? x, string? y)
        => string.Equals(Normalize(x), Normalize(y), StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(string value)
        => StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(value));

    /// <summary>
    /// 比較・辞書キーに使う正規形。パス正規化の唯一の正本。
    ///
    /// PC（ドライブ一覧）を表す <see cref="FileSystemService.ComputerPath"/> は空文字なので素通しし、
    /// それ以外は「区切り文字を <c>\</c> へ統一 → 連続区切りを1つへ畳む → 末尾区切りを除去」する。
    /// Windows は <c>/</c> と <c>\</c> を等価に扱い、<c>C:\a\\b</c> と <c>C:\a\b</c> も同じフォルダーを指すため、
    /// 統一しないと同一フォルダーが別のタブ・別の監視キー・別のフォルダー別設定として扱われてしまう。
    /// ルート（<c>C:\</c> や <c>\\server\share</c>）の区切りは意味を持つので除去しない。
    /// </summary>
    internal static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }

        var unified = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.TrimEndingDirectorySeparator(CollapseSeparators(unified));
    }

    /// <summary>連続した区切りを1つへ畳む。UNC の先頭 <c>\\</c> だけは意味を持つので保持する。</summary>
    private static string CollapseSeparators(string path)
    {
        // UNC 判定用に先頭の区切りを保ったまま、それ以降の重複だけを畳む。
        var leading = 0;
        while (leading < path.Length && path[leading] == Path.DirectorySeparatorChar)
        {
            leading++;
        }

        // 先頭が区切り2つ以上なら UNC として \\ に正規化、1つならそのまま、0 なら無し。
        var prefix = leading >= 2 ? @"\\" : path[..leading];
        var builder = new System.Text.StringBuilder(path.Length);
        builder.Append(prefix);

        var previousWasSeparator = false;
        for (var i = leading; i < path.Length; i++)
        {
            var c = path[i];
            if (c == Path.DirectorySeparatorChar)
            {
                if (!previousWasSeparator)
                {
                    builder.Append(c);
                }

                previousWasSeparator = true;
                continue;
            }

            builder.Append(c);
            previousWasSeparator = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// ドライブ直下（<c>C:\</c>）やネットワーク共有のルート（<c>\\server\share</c>）か。
    /// エクスプローラー同様、これらを移動・コピー・削除・名前の変更の対象にしないための判定。
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetPathRoot(string?)"/> の戻り値と素の <see cref="string.Equals(string?, string?, StringComparison)"/>
    /// で比べると、同じルートでも表記が違うだけで false になる。実測（.NET 10 / Windows 11）:
    /// <c>C:/</c> と <c>C:\\</c> は root が <c>C:\</c>、<c>\\server\share\</c> と <c>\\server/share</c> は
    /// root が <c>\\server\share</c> を返し、いずれも素の比較では一致しない。
    /// <para>
    /// これは机上の話ではない。<c>MainWindowViewModel.OpenShellPaths</c> は起動引数を
    /// <see cref="Path.GetFullPath(string)"/> だけ通して <c>CurrentPath</c> にするが、GetFullPath は
    /// UNC ルートの末尾区切りを落とさない（実測）。つまりシェルの「Kiriha で開く」やショートカットが
    /// <c>\\server\share\</c> を渡すと、共有ルートを開いたタブが「ルートではない」と判定され、
    /// 統一コンテキストメニューの切り取り / 削除 / 名前の変更が有効になってしまう。
    /// </para>
    /// 正規化を挟むこの判定を唯一の実装にすること（以前はドラッグ側とメニュー側に別実装があり、
    /// ドラッグは弾くのにメニューは通す、という食い違いになっていた）。
    /// </remarks>
    internal static bool IsRoot(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && Instance.Equals(root, path);
    }
}
