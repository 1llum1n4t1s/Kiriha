namespace Kiriha.Services;

/// <summary>Windowsパスを大小文字と末尾区切りを無視して比較する。</summary>
internal sealed class WindowsPathIdentity : IEqualityComparer<string>
{
    public static WindowsPathIdentity Instance { get; } = new();

    public bool Equals(string? x, string? y)
        => string.Equals(Normalize(x), Normalize(y), StringComparison.OrdinalIgnoreCase);

    public int GetHashCode(string value)
        => StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(value));

    private static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == FileSystemService.ComputerPath)
        {
            return path ?? "";
        }

        return Path.TrimEndingDirectorySeparator(path);
    }
}
