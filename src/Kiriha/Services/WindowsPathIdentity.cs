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
}
